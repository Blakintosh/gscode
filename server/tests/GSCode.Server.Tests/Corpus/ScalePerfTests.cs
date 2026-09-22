using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Server.Handlers;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// What happens to every startup and per-request cost as the workspace grows past anything a game
/// ships — see <see cref="ScaleCorpusFixture"/> for how the workspaces are built.
///
/// One row per (game, total), each measured in a fresh database: a cold index with no cache, the
/// memory it leaves behind, the compaction the server runs after it, completion and single-file
/// lint distributions against that finished index, the full-mode lint sweep, then a cache populate
/// and a warm start from it. Each is printed beside its budget from PERF.md's scale table.
///
/// Two kinds of budget, and the difference is the point of the sweep. Startup costs are allowed to
/// grow with the workspace, roughly linearly. Per-request costs — a completion, one file's lint pass
/// — are NOT: a keystroke in a 50,000-file workspace touches one file, so a number that climbs with
/// the total means something on that path is walking the whole store.
///
/// Reporting, not asserting, like <see cref="CorpusPerfTests"/>: the machine moves these numbers by
/// tens of percent. The one exception is a correctness fact rather than a timing — a warm start that
/// restores fewer files than it indexed — which is reported as a WARNING line to grep for.
///
/// Its own category so neither the everyday filter nor <c>Category=Perf</c> pays for it, and a
/// no-op unless <c>GSCODE_SCALE_SIZES</c> is set.
/// </summary>
[Trait("Category", "Scale")]
[Collection(GameProfileCollection.Name)]
public class ScalePerfTests
{
    private const string CacheIdentity = "scale-perf-identity";

    /// <summary>Files sampled for the per-request distributions. Enough for a p99 to mean something.</summary>
    private const int RequestSampleFiles = 300;

    private const int CallSitesPerFile = 5;

    private readonly ITestOutputHelper _output;

    public ScalePerfTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>One row of the report: every figure one (game, total) produced.</summary>
    private sealed class ScaleRow
    {
        public string Game { get; set; } = "";
        public int Total { get; set; }
        public double ComplexShare { get; set; }
        public double ColdSeconds { get; set; }
        public double EnumerateSeconds { get; set; }
        public double Parallelism { get; set; }
        public double RetainedMegabytes { get; set; }
        public double WorkingSetMegabytes { get; set; }
        public double CompactMilliseconds { get; set; }
        public double CompletionP50 { get; set; }
        public double CompletionP99 { get; set; }
        public double CompletionMax { get; set; }
        public double LiteralP99 { get; set; }
        public double LiteralMax { get; set; }
        public int LiteralRequests { get; set; }
        public double LintP99 { get; set; }
        public double LintMax { get; set; }
        public double LintSweepSeconds { get; set; }
        public double PopulateSeconds { get; set; }
        public double DrainSeconds { get; set; }
        public int DroppedWrites { get; set; }
        public double LoadAllMilliseconds { get; set; }
        public double LoadAllMegabytes { get; set; }
        public double WarmIndexSeconds { get; set; }
        public int WarmRestored { get; set; }
        public int WarmTotal { get; set; }
        public double DatabaseMegabytes { get; set; }

        public double WarmSeconds
        {
            get { return LoadAllMilliseconds / 1000.0 + WarmIndexSeconds; }
        }
    }

    [Fact]
    public async Task Scale_WhereTheTimeGoes()
    {
        IReadOnlyList<int> sizes = ScaleCorpusFixture.Sizes();
        IReadOnlyList<GameProfile> games = ScaleCorpusFixture.Games();
        if ( sizes.Count == 0 || games.Count == 0 )
        {
            _output.WriteLine("SKIPPED: set GSCODE_SCALE_SIZES (e.g. 10000,25000,50000) and a GSCODE_CORPUS_<GAME> for the game.");
            return;
        }

        List<ScaleRow> rows = [];
        foreach ( GameProfile game in games )
        {
            foreach ( int size in sizes )
            {
                Stopwatch generate = Stopwatch.StartNew();
                ScaleCorpus corpus = ScaleCorpusFixture.Ensure(game, size);
                generate.Stop();
                _output.WriteLine(
                    $"########## {game.ShortName} {corpus.TotalFiles:N0} files ({corpus.CopiedFiles:N0} copied, "
                    + $"{corpus.ComplexShare * 100:F0}% above-median size) — workspace ready in {generate.Elapsed.TotalSeconds:F1} s");

                ScaleRow row = await MeasureAsync(corpus);
                rows.Add(row);
                WriteRow(row);
            }
        }

        WriteReport(rows);
    }

    private async Task<ScaleRow> MeasureAsync(ScaleCorpus corpus)
    {
        GameProfile previous = GameProfile.Active;
        string databasePath = Path.Combine(Path.GetTempPath(), $"gscode-scale-{Guid.NewGuid():N}.db");

        try
        {
            GameProfile.Select(corpus.Profile.ShortName);
            ScaleRow row = new() { Game = corpus.Profile.ShortName, Total = corpus.TotalFiles, ComplexShare = corpus.ComplexShare };

            PerfReport.Memory baseline = PerfReport.Sample();

            // Cold, no cache — and the finished database is kept for everything that needs one.
            PathResolver resolver = ScaleCorpusFixture.Resolver(corpus);
            NameTable names = new();
            ScriptDatabase database = new();
            InsertCache inserts = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names, inserts);

            Stopwatch cold = Stopwatch.StartNew();
            IndexOutcome coldOutcome = await indexer.IndexAsync(
                IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
            cold.Stop();
            row.ColdSeconds = cold.Elapsed.TotalSeconds;
            row.EnumerateSeconds = coldOutcome.Enumerate.TotalSeconds;
            row.Parallelism = coldOutcome.Parallelism;

            // The server's own post-index compaction, timed: it is a blocking gen2 over the whole
            // heap, so its pause grows with what the index retained.
            Stopwatch compact = Stopwatch.StartNew();
            Compact();
            compact.Stop();
            row.CompactMilliseconds = compact.Elapsed.TotalMilliseconds;

            PerfReport.Memory afterIndex = PerfReport.Sample();
            row.RetainedMegabytes = (afterIndex.ManagedLive - baseline.ManagedLive) / 1048576.0;
            row.WorkingSetMegabytes = afterIndex.WorkingSet / 1048576.0;

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);

            List<string> sample = Sample(ScaleCorpusFixture.CopiedScripts(corpus), RequestSampleFiles);
            MeasureRequests(row, corpus, sample, database, resolver, names, inserts, builtins, objectFields);

            row.LintSweepSeconds = await MeasureLintSweepAsync(database, indexer, resolver, builtins, objectFields);

            GC.KeepAlive(database);
            database = null!;
            indexer = null!;

            // Populate a cache, then start warm from it with nothing else carried across.
            int dropped;
            {
                ScriptDatabase populateDatabase = new();
                PathResolver populateResolver = ScaleCorpusFixture.Resolver(corpus);
                WorkspaceIndexer populateIndexer = new(
                    populateDatabase, () => populateResolver, new PhysicalFileSystem(), new NameTable());

                await using SqliteCache populateCache = SqliteCache.Open(databasePath, CacheIdentity);
                populateIndexer.UseCache(populateCache, populateCache.LoadAll());

                Stopwatch populate = Stopwatch.StartNew();
                await populateIndexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
                populate.Stop();

                Stopwatch drain = Stopwatch.StartNew();
                await populateCache.WaitForIdleAsync(CancellationToken.None);
                drain.Stop();

                row.PopulateSeconds = populate.Elapsed.TotalSeconds;
                row.DrainSeconds = drain.Elapsed.TotalSeconds;
                dropped = populateCache.DroppedWrites;
            }

            row.DroppedWrites = dropped;
            PerfReport.Sample();

            {
                ScriptDatabase warmDatabase = new();
                PathResolver warmResolver = ScaleCorpusFixture.Resolver(corpus);
                WorkspaceIndexer warmIndexer = new(warmDatabase, () => warmResolver, new PhysicalFileSystem(), new NameTable());

                await using SqliteCache warmCache = SqliteCache.Open(databasePath, CacheIdentity);

                Stopwatch load = Stopwatch.StartNew();
                IReadOnlyDictionary<string, CachedEntry> restored = warmCache.LoadAll();
                load.Stop();

                long blobBytes = 0;
                foreach ( CachedEntry entry in restored.Values )
                {
                    blobBytes += entry.Blob.Length;
                }

                warmIndexer.UseCache(warmCache, restored);
                restored = null!;

                Stopwatch warm = Stopwatch.StartNew();
                IndexOutcome warmOutcome = await warmIndexer.IndexAsync(
                    IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
                warm.Stop();

                row.LoadAllMilliseconds = load.Elapsed.TotalMilliseconds;
                row.LoadAllMegabytes = blobBytes / 1048576.0;
                row.WarmIndexSeconds = warm.Elapsed.TotalSeconds;
                row.WarmRestored = warmOutcome.Restored;
                row.WarmTotal = warmOutcome.Total;

                await warmCache.WaitForIdleAsync(CancellationToken.None);
            }

            row.DatabaseMegabytes = DatabaseBytes(databasePath) / 1048576.0;
            return row;
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
            SqliteCache.DeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// Completion and single-file lint distributions over a sample of the copied files. Completion is
    /// timed at file scope, at call sites, and — separately — inside a string literal, since that arm
    /// collects literals from every record in the workspace and is the one expected to grow with it.
    /// </summary>
    private static void MeasureRequests(
        ScaleRow row, ScaleCorpus corpus, List<string> sample, ScriptDatabase database, PathResolver resolver,
        NameTable names, InsertCache inserts, BuiltinApiSet builtins, ObjectFields objectFields)
    {
        CompletionEngine engine = new(database, builtins, objectFields);
        List<double> completions = [];
        List<double> literals = [];
        List<double> lints = [];

        foreach ( string path in sample )
        {
            ParseResult parsed;
            try
            {
                parsed = ScriptAnalysis.Analyze(
                    path,
                    corpus.Profile.LanguageFromPath(path),
                    SourceText.From(File.ReadAllText(path)),
                    new ResolverInsertProvider(resolver, resolver.GetContext(path), new PhysicalFileSystem(), inserts),
                    names,
                    corpus.Profile,
                    inserts);
            }
            catch ( Exception )
            {
                continue;
            }

            string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));
            ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

            foreach ( Position position in CallPositions(parsed) )
            {
                completions.Add(TimeCompletion(engine, parsed, contextId, position, corpus.Profile));
            }

            Position? literal = LiteralPosition(parsed);
            if ( literal is not null )
            {
                literals.Add(TimeCompletion(engine, parsed, contextId, literal.Value, corpus.Profile));
            }

            if ( language is ScriptLanguage.Gsc or ScriptLanguage.Csc )
            {
                WorkspaceLints.LintsOnly(parsed, language, path, database, resolver, builtins, objectFields);
                Stopwatch lint = Stopwatch.StartNew();
                WorkspaceLints.LintsOnly(parsed, language, path, database, resolver, builtins, objectFields);
                lint.Stop();
                lints.Add(lint.Elapsed.TotalMilliseconds);
            }
        }

        completions.Sort();
        literals.Sort();
        lints.Sort();

        row.CompletionP50 = Percentile(completions, 0.50);
        row.CompletionP99 = Percentile(completions, 0.99);
        row.CompletionMax = completions.Count == 0 ? 0 : completions[^1];
        row.LiteralRequests = literals.Count;
        row.LiteralP99 = Percentile(literals, 0.99);
        row.LiteralMax = literals.Count == 0 ? 0 : literals[^1];
        row.LintP99 = Percentile(lints, 0.99);
        row.LintMax = lints.Count == 0 ? 0 : lints[^1];
    }

    /// <summary>
    /// The full-mode workspace lint sweep's work — re-read, re-analyse and lint every GSC/CSC record
    /// at <c>ProcessorCount - 1</c> — without the server's DI graph. Same indexer method, same lints.
    /// </summary>
    private static async Task<double> MeasureLintSweepAsync(
        ScriptDatabase database, WorkspaceIndexer indexer, PathResolver resolver,
        BuiltinApiSet builtins, ObjectFields objectFields)
    {
        List<ScriptRecord> targets = [.. database.Gsc.AllRecords, .. database.Csc.AllRecords];
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

        Stopwatch sweep = Stopwatch.StartNew();
        await Parallel.ForEachAsync(targets, options, (record, token) =>
        {
            ParseResult? result = indexer.AnalyzeForLintSweep(record.Path);
            if ( result is not null )
            {
                WorkspaceLints.LintsOnly(result, record.Language, record.Path, database, resolver, builtins, objectFields);
            }

            return ValueTask.CompletedTask;
        });
        sweep.Stop();

        return sweep.Elapsed.TotalSeconds;
    }

    private static double TimeCompletion(
        CompletionEngine engine, ParseResult parsed, string contextId, Position position, GameProfile profile)
    {
        // Warm first, as CorpusPerfTests does, so lazily built state is not charged to the request.
        Complete(engine, parsed, contextId, position, profile);

        Stopwatch watch = Stopwatch.StartNew();
        Complete(engine, parsed, contextId, position, profile);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static void Complete(
        CompletionEngine engine, ParseResult parsed, string contextId, Position position, GameProfile profile)
    {
        engine.Complete(
            parsed,
            contextId,
            position,
            includeLiterals: true,
            fieldScope: FieldScope.Owner,
            callPunctuation: CallPunctuation.Parens,
            profile: profile,
            parameterHints: true);
    }

    /// <summary>File scope plus evenly spaced call sites — the statement-scope arm, as CorpusPerfTests samples it.</summary>
    private static List<Position> CallPositions(ParseResult parsed)
    {
        List<Position> positions = [new Position(0, 0)];
        List<Position> calls = [];
        foreach ( ReferenceEntry entry in parsed.Extraction.References )
        {
            if ( entry.Kind == ReferenceKind.Call )
            {
                calls.Add(entry.Range.End);
            }
        }

        if ( calls.Count <= CallSitesPerFile )
        {
            positions.AddRange(calls);
            return positions;
        }

        for ( int index = 0; index < CallSitesPerFile; index++ )
        {
            positions.Add(calls[index * calls.Count / CallSitesPerFile]);
        }

        return positions;
    }

    /// <summary>Just inside the first single-line string literal, where literal completion runs.</summary>
    private static Position? LiteralPosition(ParseResult parsed)
    {
        foreach ( ReferenceEntry entry in parsed.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Literal )
            {
                continue;
            }

            TextRange range = entry.Range;
            if ( range.Start.Line == range.End.Line && range.End.Character - range.Start.Character >= 2 )
            {
                return new Position(range.Start.Line, range.Start.Character + 1);
            }
        }

        return null;
    }

    private static List<string> Sample(List<string> files, int count)
    {
        if ( files.Count <= count )
        {
            return files;
        }

        List<string> sample = [];
        for ( int index = 0; index < count; index++ )
        {
            sample.Add(files[(int)((long)index * files.Count / count)]);
        }

        return sample;
    }

    /// <summary>The server's post-index compaction (StartupIndexRunner.Compact), reproduced so its pause can be timed.</summary>
    private static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        if ( sorted.Count == 0 )
        {
            return 0;
        }

        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    private static long DatabaseBytes(string databasePath)
    {
        long total = 0;
        foreach ( string suffix in new[] { "", "-wal", "-shm" } )
        {
            FileInfo file = new(databasePath + suffix);
            if ( file.Exists )
            {
                total += file.Length;
            }
        }

        return total;
    }

    // Budgets from PERF.md's scale table, interpolated linearly between its points. Startup budgets
    // grow with the workspace; the per-request ones below are flat by design.
    private static readonly (int Files, double Value)[] ColdBudget = [(1000, 1), (10000, 8), (25000, 18), (50000, 35)];
    private static readonly (int Files, double Value)[] WarmBudget = [(1000, 1), (10000, 3), (25000, 6), (50000, 10)];
    private static readonly (int Files, double Value)[] SweepBudget = [(1000, 2), (10000, 10), (25000, 25), (50000, 60)];
    private static readonly (int Files, double Value)[] MemoryBudget = [(1000, 50), (10000, 600), (25000, 1500), (50000, 3000)];

    /// <summary>Completion p99, flat: low single-digit milliseconds today at every stock size.</summary>
    private const double CompletionBudgetMilliseconds = 10;

    /// <summary>One file's whole lint pass, flat: the LintBudgetTests share of the debounce.</summary>
    private static readonly double LintBudgetMilliseconds = AnalysisTiming.DebounceMilliseconds * 0.40;

    private static double BudgetAt((int Files, double Value)[] points, int files)
    {
        if ( files <= points[0].Files )
        {
            return points[0].Value;
        }

        for ( int index = 1; index < points.Length; index++ )
        {
            if ( files <= points[index].Files )
            {
                double fraction = (double)(files - points[index - 1].Files) / (points[index].Files - points[index - 1].Files);
                return points[index - 1].Value + fraction * (points[index].Value - points[index - 1].Value);
            }
        }

        double last = points[^1].Value;
        return last * files / points[^1].Files;
    }

    private static string Verdict(double value, double budget)
    {
        return value <= budget ? "ok  " : "OVER";
    }

    private void WriteRow(ScaleRow row)
    {
        double coldBudget = BudgetAt(ColdBudget, row.Total);
        double warmBudget = BudgetAt(WarmBudget, row.Total);
        double sweepBudget = BudgetAt(SweepBudget, row.Total);
        double memoryBudget = BudgetAt(MemoryBudget, row.Total);

        _output.WriteLine($"     cold index        {row.ColdSeconds,8:F1} s   budget {coldBudget,6:F1} s   {Verdict(row.ColdSeconds, coldBudget)}  (find {row.EnumerateSeconds:F1} s, {row.Parallelism:F1}x parallel)");
        _output.WriteLine($"     warm start        {row.WarmSeconds,8:F1} s   budget {warmBudget,6:F1} s   {Verdict(row.WarmSeconds, warmBudget)}  (LoadAll {row.LoadAllMilliseconds:F0} ms / {row.LoadAllMegabytes:F0} MB, index {row.WarmIndexSeconds:F1} s)");
        _output.WriteLine($"     retained memory   {row.RetainedMegabytes,8:F0} MB  budget {memoryBudget,6:F0} MB  {Verdict(row.RetainedMegabytes, memoryBudget)}  (soft; working set {row.WorkingSetMegabytes:F0} MB)");
        _output.WriteLine($"     compaction pause  {row.CompactMilliseconds,8:F0} ms");
        _output.WriteLine($"     lint sweep        {row.LintSweepSeconds,8:F1} s   budget {sweepBudget,6:F1} s   {Verdict(row.LintSweepSeconds, sweepBudget)}");
        _output.WriteLine($"     completion p99    {row.CompletionP99,8:F2} ms  budget {CompletionBudgetMilliseconds,6:F1} ms  {Verdict(row.CompletionP99, CompletionBudgetMilliseconds)}  (p50 {row.CompletionP50:F2}, max {row.CompletionMax:F1})");
        _output.WriteLine($"     literal compl p99 {row.LiteralP99,8:F2} ms  budget {CompletionBudgetMilliseconds,6:F1} ms  {Verdict(row.LiteralP99, CompletionBudgetMilliseconds)}  ({row.LiteralRequests} requests, max {row.LiteralMax:F1})");
        _output.WriteLine($"     one-file lint max {row.LintMax,8:F1} ms  budget {LintBudgetMilliseconds,6:F1} ms  {Verdict(row.LintMax, LintBudgetMilliseconds)}  (p99 {row.LintP99:F1})");
        _output.WriteLine($"     cache populate    {row.PopulateSeconds,8:F1} s   + drain {row.DrainSeconds:F1} s, db {row.DatabaseMegabytes:F0} MB");

        if ( row.DroppedWrites > 0 )
        {
            _output.WriteLine($"     WARNING: {row.DroppedWrites:N0} cache write(s) dropped while populating");
        }

        if ( row.WarmRestored != row.WarmTotal )
        {
            _output.WriteLine($"     WARNING: warm start restored {row.WarmRestored:N0} of {row.WarmTotal:N0} files");
        }
    }

    /// <summary>A markdown table under <c>temp/</c>, ready to paste into PERF.md.</summary>
    private void WriteReport(List<ScaleRow> rows)
    {
        StringBuilder table = new();
        table.AppendLine("| game | files | cold s | warm s | LoadAll ms | retained MB | compact ms | sweep s | compl p99 ms | literal p99 ms | lint max ms | dropped | restored |");
        table.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach ( ScaleRow row in rows )
        {
            table.AppendLine(
                $"| {row.Game} | {row.Total:N0} | {row.ColdSeconds:F1} | {row.WarmSeconds:F1} | {row.LoadAllMilliseconds:F0} | "
                + $"{row.RetainedMegabytes:F0} | {row.CompactMilliseconds:F0} | {row.LintSweepSeconds:F1} | {row.CompletionP99:F2} | "
                + $"{row.LiteralP99:F2} | {row.LintMax:F1} | {row.DroppedWrites:N0} | {row.WarmRestored:N0}/{row.WarmTotal:N0} |");
        }

        string directory = Environment.GetEnvironmentVariable("GSCODE_PERF_REPORT") is string configured && configured.Length > 0
            ? configured
            : ScratchDirectory();
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "gscode-scale.md");
        File.WriteAllText(path, table.ToString());

        _output.WriteLine("");
        _output.WriteLine(table.ToString());
        _output.WriteLine($"Report: {path}");
    }

    private static string ScratchDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while ( current is not null )
        {
            string git = Path.Combine(current.FullName, ".git");
            if ( Directory.Exists(git) || File.Exists(git) )
            {
                return Path.Combine(current.FullName, "temp");
            }

            current = current.Parent;
        }

        return Path.GetTempPath();
    }
}
