using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// Where analysis time actually goes, per FILE, over a real game's scripts.
///
/// A whole-run total answers nothing useful: 7,000 files at a few milliseconds each is the same
/// number as 6,900 fast ones and 100 pathological ones, and only the second is worth fixing. So this
/// times each file, then reports the distribution and the slowest by both absolute time and time per
/// kilobyte — the second being what separates a genuinely slow file from a merely large one.
///
/// Reporting, not asserting. A timing threshold on a machine of unknown speed is a flaky test; the
/// budget gates live in CorpusTests, which assert throughput the whole run has to meet. This exists
/// to be READ.
/// </summary>
[Trait("Category", "Perf")]
[Collection(GameProfileCollection.Name)]
public class CorpusPerfTests
{
    /// <summary>
    /// Stands in for <c>ServerBuildIdentity.Compute</c>, which keys the real cache on the bundled
    /// data files. Any constant works here as long as both opens agree: a mismatch makes
    /// <c>SqliteCache.Open</c> discard the database, and the "warm" run would silently be a cold one.
    /// </summary>
    private const string WarmCacheIdentity = "warm-perf-identity";

    private readonly ITestOutputHelper _output;

    public CorpusPerfTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BlackOps3_WhereTheTimeGoes()
    {
        if ( !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: %GSCODE_CORPUS_BO3% not found - the perf sweep needs the game scripts.");
            return;
        }

        PathResolver resolver = CorpusFixture.Resolver();
        NameTable names = new();
        List<PerfReport.Item> timings = [];

        foreach ( string path in CorpusFixture.Scripts() )
        {
            ResolverInsertProvider inserts = new(resolver, resolver.GetContext(path), new PhysicalFileSystem(), CorpusFixture.Inserts);
            timings.Add(Time(path, GameProfile.BlackOps3, inserts, names, CorpusFixture.Inserts, () => CorpusFixture.Analyze(path, resolver, names)));
        }

        Report("bo3", timings, CorpusFixture.RawRoot!, CorpusFixture.Inserts.Count);
    }

    [Fact]
    public void EveryOtherGame_WhereTheTimeGoes()
    {
        IReadOnlyList<GameCorpus> corpora = GameCorpusFixture.Available();
        if ( corpora.Count == 0 )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_<GAME>% found - the perf sweep needs the game scripts.");
            return;
        }

        foreach ( GameCorpus corpus in corpora )
        {
            PathResolver resolver = GameCorpusFixture.Resolver(corpus);
            NameTable names = new();
            List<PerfReport.Item> timings = [];

            foreach ( string path in GameCorpusFixture.Scripts(corpus) )
            {
                ResolverInsertProvider inserts = new(resolver, resolver.GetContext(path), new PhysicalFileSystem(), GameCorpusFixture.Inserts);
                timings.Add(Time(path, corpus.Profile, inserts, names, GameCorpusFixture.Inserts, () => GameCorpusFixture.Analyze(corpus, path, resolver, names)));
            }

            Report(corpus.Profile.ShortName, timings, corpus.RawRoot, GameCorpusFixture.Inserts.Count);
        }
    }

    /// <summary>
    /// Where a COLD index spends its time — the path that runs before any cache exists, and the one
    /// the &lt; 60 s budget in PERF.md is about.
    ///
    /// Nothing timed it until now. The only recorded cold figure was read by hand off the server's
    /// own log line, and the sweeps in this class build an index solely as a precondition, outside
    /// every stopwatch. So a change to enumeration, reading, the analysis pipeline or the commit path
    /// could not be attributed to anything.
    ///
    /// Cold means NO CACHE: `UseCache` is never called, so every file is read and analysed and none
    /// is restored. That is deliberate — a warm run measures the restore path instead, which is a
    /// different question with a different answer.
    ///
    /// The scope breakdown (enumerate / read / analyse / commit / enqueue) needs
    /// `-p:GscodeInstrumentation=true`; without it the totals still stand and the table says so.
    /// Enumeration is the one to watch: it is fully serial and blocks every worker, so it shows up
    /// as wall-clock with no parallelism behind it.
    ///
    /// Reporting, not asserting, like the rest of this class — and one run does not support a point.
    /// Totals here swing by tens of percent; take three and compare ranges.
    /// </summary>
    [Fact]
    public async Task ColdIndex_WhereTheTimeGoes()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await MeasureColdIndexAsync(GameProfile.BlackOps3, CorpusFixture.Resolver);
            measured = true;
        }

        // CoD4 for the #include dialect, and BO1 because enumeration cost is not a function of
        // script count: its raw folder holds 160,382 files to CoD4's few thousand, so it is the
        // only corpus where the tree walk is a visible share of a cold index.
        foreach ( GameProfile profile in new[] { GameProfile.Cod4, GameProfile.BlackOps } )
        {
            GameCorpus? corpus = GameCorpusFixture.For(profile);
            if ( corpus is null )
            {
                continue;
            }

            GameCorpus captured = corpus;
            await MeasureColdIndexAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_BO3%, %GSCODE_CORPUS_COD4% or %GSCODE_CORPUS_BO1% found.");
        }
    }

    private async Task MeasureColdIndexAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            // Everything per-run: a fresh resolver, NameTable and database, so no interning or
            // record from a previous game or a previous run is doing any of the work.
            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);

            PerfTracker.Reset();

            Stopwatch watch = Stopwatch.StartNew();
            IndexOutcome outcome = await indexer.IndexAsync(
                IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
            watch.Stop();

            Dictionary<string, (double Milliseconds, long Count)> scopes = [];
            PerfTracker.Snapshot(scopes);

            _output.WriteLine("");
            _output.WriteLine(
                $"########## {profile.ShortName} cold index: {outcome.Total} files in {watch.Elapsed.TotalMilliseconds:F0} ms "
                + $"({outcome.Restored} restored, {outcome.SkippedOversized} oversized)");

            if ( scopes.Count == 0 )
            {
                _output.WriteLine("     scopes: not instrumented (rebuild with -p:GscodeInstrumentation=true)");
                return;
            }

            // Two denominators, because the scopes are not on one clock. Per-file scopes are summed
            // across ProcessorCount - 1 threads, so dividing them by wall-clock produces percentages
            // over 100 and means nothing; they are shares of the THREAD-TIME total. index.enumerate
            // is serial and holds wall-clock on its own, so it is reported against that instead.
            //
            // index.read/analyse/commit/enqueue are the top-level per-file stages. Anything else
            // (extract.* inside analyse, commit.* inside commit) is a sub-scope, so it is listed but
            // not summed — adding it to the total would count the same microseconds twice.
            string[] topLevel = ["index.read", "index.analyse", "index.commit", "index.enqueue", "index.restore"];
            double threadTime = scopes.Where(s => topLevel.Contains(s.Key)).Sum(s => s.Value.Milliseconds);

            foreach ( KeyValuePair<string, (double Milliseconds, long Count)> scope in scopes
                .Where(s => s.Key != "index.total")
                .OrderByDescending(s => s.Value.Milliseconds) )
            {
                bool nested = !topLevel.Contains(scope.Key) && scope.Key != "index.enumerate";
                string share = scope.Key == "index.enumerate"
                    ? $"{scope.Value.Milliseconds / watch.Elapsed.TotalMilliseconds * 100,5:F1}% of WALL (serial)"
                    : $"{scope.Value.Milliseconds / threadTime * 100,5:F1}% of thread-time{(nested ? " (nested)" : "")}";

                _output.WriteLine(
                    $"     {scope.Key,-20} {scope.Value.Milliseconds,8:F0} ms  {scope.Value.Count,7:N0} calls  {share}");
            }

            // The ratio of thread-time to wall-clock is the parallel speedup actually achieved. A
            // serial stage shows up by holding wall-clock while contributing nothing to it.
            _output.WriteLine(
                $"     thread-time {threadTime:F0} ms over wall-clock {watch.Elapsed.TotalMilliseconds:F0} ms "
                + $"= {(threadTime <= 0 ? 0 : threadTime / watch.Elapsed.TotalMilliseconds):F1}x parallel "
                + $"(ceiling {Math.Max(1, Environment.ProcessorCount - 1)}x)");

            PerfReport.Memory memory = PerfReport.Sample();
            _output.WriteLine(
                $"     memory: live {memory.ManagedLive / 1048576.0:F0} MB | heap {memory.HeapSize / 1048576.0:F0} MB | "
                + $"fragmented {memory.Fragmented / 1048576.0:F0} MB | working set {memory.WorkingSet / 1048576.0:F0} MB");
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// Where a WARM start spends its time — the path a user takes on every start after their
    /// first, and the one nothing in this class has ever timed.
    ///
    /// <see cref="ColdIndex_WhereTheTimeGoes"/> attaches no cache on purpose, and its comment says
    /// why: "a warm run measures the restore path instead, which is a different question with a
    /// different answer". That question was then never asked. Everything measured since has landed
    /// on the cold arm — the server GC, the pruned enumeration, the one-pass reader — which took a
    /// BO3 cold index from 2.6 s to under one. The arm that got none of it is now the one worth
    /// looking at, and the last warm figure on record (2.6 s) predates all three.
    ///
    /// The cache read is timed SEPARATELY from the index, because they are not the same shape and
    /// one number cannot say which to attack. It is what found the problem: <c>LoadAll</c> was a
    /// single thread gzip-inflating and JSON-parsing every record to completion before
    /// <c>IndexAsync</c> was called at all, and in the server it was not merely unsplit but
    /// entirely OUTSIDE the stopwatch, being an argument to the <c>UseCache</c> call that precedes
    /// the timed block. Measured here for the first time it was 91% of a warm start.
    ///
    /// It now reads blobs only, so this stage is the SQLite read and the deserialize shows up under
    /// <c>index.restore</c> on the indexing threads. Keep both numbers: the point of the split is
    /// that either half can regress on its own.
    ///
    /// Two indexes per game. The first exists only to leave a populated database behind, and its
    /// drain is not optional: the writer serializes and gzips on its own thread well after
    /// <c>IndexAsync</c> returns, so without <c>WaitForIdleAsync</c> the measured run restores
    /// whatever happened to have been flushed and reports a warm start that is half cold.
    /// </summary>
    [Fact]
    public async Task WarmIndex_WhereTheTimeGoes()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await MeasureWarmIndexAsync(GameProfile.BlackOps3, CorpusFixture.Resolver);
            measured = true;
        }

        // The same two as the cold sweep, and for the same reasons: CoD4 is the #include dialect,
        // and BO1 is the only corpus large enough for a per-record cost to be visible.
        foreach ( GameProfile profile in new[] { GameProfile.Cod4, GameProfile.BlackOps } )
        {
            GameCorpus? corpus = GameCorpusFixture.For(profile);
            if ( corpus is null )
            {
                continue;
            }

            GameCorpus captured = corpus;
            await MeasureWarmIndexAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_BO3%, %GSCODE_CORPUS_COD4% or %GSCODE_CORPUS_BO1% found.");
        }
    }


    /// <summary>
    /// What the cache costs BEFORE indexing starts, which is the part of a warm start that runs on
    /// the thread the server is starting on.
    ///
    /// `Program.cs` does four things between `OnStarted` firing and the indexing task being queued:
    /// sweeps the legacy cache directory, hashes the bundled data files into a build identity,
    /// opens the database, and reads the blobs. All four are ahead of the `Task.Run`, so they are
    /// startup latency rather than indexing, and the server's log rolls them into one `cache 0.1s`
    /// figure that cannot say which of the four to attack.
    /// </summary>
    [Fact]
    public async Task CacheOpen_WhereTheStartupTimeGoes()
    {
        if ( !CorpusFixture.Available && !GameCorpusFixture.Available().Any() )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_<GAME>% found.");
            return;
        }

        string databasePath = Path.Combine(Path.GetTempPath(), $"gscode-startup-{Guid.NewGuid():N}.db");

        try
        {
            // Warm the file cache and the JIT the way a second start would find them; the first
            // read of a 2.8 MB data file off cold disk is a different question.
            _ = ServerBuildIdentity.Compute(BundledDataFilePaths(), GameProfile.Active.ShortName);

            Stopwatch watch = Stopwatch.StartNew();
            SqliteCache.CleanUpLegacyCache();
            double sweep = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            string identity = ServerBuildIdentity.Compute(BundledDataFilePaths(), GameProfile.Active.ShortName);
            double fingerprint = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            SqliteCache cache = SqliteCache.Open(databasePath, identity);
            double open = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            int rows = cache.LoadAll().Count;
            double read = watch.Elapsed.TotalMilliseconds;

            await cache.DisposeAsync();

            // A SECOND open, which is what splits the number above. Microsoft.Data.Sqlite loads its
            // native provider on first use, so the first Open in a process pays an assembly load, a
            // native library load and the JIT behind them; every one after it pays the file.
            string secondPath = Path.Combine(Path.GetTempPath(), $"gscode-startup2-{Guid.NewGuid():N}.db");
            watch.Restart();
            SqliteCache second = SqliteCache.Open(secondPath, identity);
            double reopen = watch.Elapsed.TotalMilliseconds;
            await second.DisposeAsync();
            try
            {
                File.Delete(secondPath);
            }
            catch ( IOException )
            {
            }

            _output.WriteLine("");
            _output.WriteLine($"########## {GameProfile.Active.ShortName} cache open, empty database, {rows} rows");
            _output.WriteLine($"     legacy sweep   {sweep,8:F1} ms");
            _output.WriteLine($"     build identity {fingerprint,8:F1} ms   {DataFileBytes() / 1048576.0:F1} MB hashed");
            _output.WriteLine($"     open           {open,8:F1} ms");
            _output.WriteLine($"     LoadAll        {read,8:F1} ms");
            _output.WriteLine($"     open (2nd)     {reopen,8:F1} ms");
            _output.WriteLine($"     total          {sweep + fingerprint + open + read,8:F1} ms");
        }
        finally
        {
            try
            {
                File.Delete(databasePath);
            }
            catch ( IOException )
            {
            }
        }
    }

    private static IEnumerable<string> BundledDataFilePaths()
    {
        string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
        foreach ( string fileName in GameProfile.Active.BundledDataFileNames )
        {
            yield return Path.Combine(apiDirectory, fileName);
        }
    }

    private static long DataFileBytes()
    {
        long total = 0;
        foreach ( string path in BundledDataFilePaths() )
        {
            if ( File.Exists(path) )
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private async Task MeasureWarmIndexAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        string databasePath = Path.Combine(Path.GetTempPath(), $"gscode-warm-{Guid.NewGuid():N}.db");

        try
        {
            GameProfile.Select(profile.ShortName);

            int dropped = await PopulateCacheAsync(databasePath, resolverFactory);

            // Fresh everything, so nothing the populating run interned, resolved or committed is
            // available to the measured one. Only the database file crosses between them.
            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);

            await using SqliteCache cache = SqliteCache.Open(databasePath, WarmCacheIdentity);

            PerfTracker.Reset();

            Stopwatch restoreWatch = Stopwatch.StartNew();
            IReadOnlyDictionary<string, CachedEntry> restored = cache.LoadAll();
            restoreWatch.Stop();

            indexer.UseCache(cache, restored);

            Stopwatch indexWatch = Stopwatch.StartNew();
            IndexOutcome outcome = await indexer.IndexAsync(
                IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
            indexWatch.Stop();

            double restoreMs = restoreWatch.Elapsed.TotalMilliseconds;
            double indexMs = indexWatch.Elapsed.TotalMilliseconds;
            double startMs = restoreMs + indexMs;
            long databaseBytes = DatabaseBytes(databasePath);

            _output.WriteLine("");
            _output.WriteLine(
                $"########## {profile.ShortName} warm start: {outcome.Total} files in {startMs:F0} ms "
                + $"({outcome.Restored} restored, {outcome.Total - outcome.Restored} re-analysed)");

            // Two stages, and the split moved once the deserialize did. This first one is now the
            // SQLite read alone — blobs off the connection, nothing inflated — so a large figure
            // here means the database itself is slow, not that the records are expensive. The
            // records became expensive inside the index instead, under `index.restore`.
            _output.WriteLine(
                $"     cache read (serial) {restoreMs,8:F0} ms  {restored.Count,7:N0} records  "
                + $"{restoreMs / startMs * 100,5:F1}% of warm start");
            _output.WriteLine(
                $"     index               {indexMs,8:F0} ms  {outcome.Total,7:N0} files    "
                + $"{indexMs / startMs * 100,5:F1}% of warm start");
            _output.WriteLine(
                $"     per record          {(restored.Count == 0 ? 0 : restoreMs / restored.Count * 1000),8:F0} us  "
                + $"cache file {databaseBytes / 1048576.0:F1} MB");

            // Not fatal, but it makes every number above a mixture: a dropped write is a file the
            // populating run never persisted, so the measured run re-analysed it and charged the
            // time to the index rather than to the restore.
            if ( dropped > 0 || outcome.Restored != outcome.Total )
            {
                _output.WriteLine(
                    $"     WARNING: not fully warm - {dropped:N0} write(s) dropped while populating, "
                    + $"{outcome.Total - outcome.Restored:N0} file(s) re-analysed. Read the split with that in mind.");
            }

            Dictionary<string, (double Milliseconds, long Count)> scopes = [];
            PerfTracker.Snapshot(scopes);

            if ( scopes.Count == 0 )
            {
                _output.WriteLine("     scopes: not instrumented (rebuild with -p:GscodeInstrumentation=true)");
            }
            else
            {
                // Same two denominators as the cold sweep, and the same reason. index.restore is
                // the per-file hash-and-commit inside IndexAsync, which is NOT the LoadAll above:
                // one is the deserialize, the other is the freshness check that decides whether the
                // deserialized record may be used at all.
                string[] topLevel = ["index.read", "index.analyse", "index.commit", "index.enqueue", "index.restore"];
                double threadTime = scopes.Where(s => topLevel.Contains(s.Key)).Sum(s => s.Value.Milliseconds);

                foreach ( KeyValuePair<string, (double Milliseconds, long Count)> scope in scopes
                    .Where(s => s.Key != "index.total")
                    .OrderByDescending(s => s.Value.Milliseconds) )
                {
                    bool nested = !topLevel.Contains(scope.Key) && scope.Key != "index.enumerate";
                    string share = scope.Key == "index.enumerate"
                        ? $"{scope.Value.Milliseconds / indexMs * 100,5:F1}% of INDEX WALL (serial)"
                        : $"{scope.Value.Milliseconds / threadTime * 100,5:F1}% of thread-time{(nested ? " (nested)" : "")}";

                    _output.WriteLine(
                        $"     {scope.Key,-20} {scope.Value.Milliseconds,8:F0} ms  {scope.Value.Count,7:N0} calls  {share}");
                }
            }

            PerfReport.Memory memory = PerfReport.Sample();
            _output.WriteLine(
                $"     memory: live {memory.ManagedLive / 1048576.0:F0} MB | heap {memory.HeapSize / 1048576.0:F0} MB | "
                + $"fragmented {memory.Fragmented / 1048576.0:F0} MB | working set {memory.WorkingSet / 1048576.0:F0} MB");
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
            SqliteCache.DeleteDatabase(databasePath);
        }
    }

    /// <summary>
    /// Indexes once into a fresh cache and waits for the writer to drain, so the database is
    /// complete before anything reads it. Returns the writes the channel refused, which is the
    /// difference between a warm start and a warm start that quietly re-analyses part of the tree.
    /// </summary>
    private static async Task<int> PopulateCacheAsync(string databasePath, Func<PathResolver> resolverFactory)
    {
        PathResolver resolver = resolverFactory();
        NameTable names = new();
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);

        await using SqliteCache cache = SqliteCache.Open(databasePath, WarmCacheIdentity);
        indexer.UseCache(cache, cache.LoadAll());

        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
        await cache.WaitForIdleAsync(CancellationToken.None);

        return cache.DroppedWrites;
    }

    /// <summary>The cache and its two SQLite side files, which is what a user's disk actually holds.</summary>
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

    /// <summary>
    /// Where the CROSS-FILE LINT time goes — the layer the two sweeps above do not touch at all.
    ///
    /// They time <c>ScriptAnalysis.Analyze</c>, whose four phases are lex, preprocess, parse and
    /// extract. Everything the editor runs ON TOP of that parse — the include-closure walk, the
    /// store lookups, the arity and resolution rules — was unmeasured, and it is the half that grew:
    /// a keystroke now pays a transitive graph walk and several name lookups that a parse-only
    /// measurement reports as free.
    ///
    /// The parse is done BEFORE the stopwatch and reused, so this is lint cost and nothing else, and
    /// the lints run once to warm before the timed pass for the same reason the parse sweep does.
    ///
    /// Needs a finished index, unlike the sweeps above: two of the heaviest rules stand down without
    /// one, so measuring against a partial index would report the cheap half and call it the total.
    ///
    /// CoD4 and BO3 only. They are the two dialect families — CoD4 the <c>#include</c> shape with no
    /// headers, BO3 the <c>#using</c> shape with <c>#insert</c> — and the other three add runtime
    /// without adding a shape. That is also what keeps this affordable beside the parse sweeps, since
    /// each game here pays a full index first.
    ///
    /// Reporting, not asserting, like the rest of this class.
    /// </summary>
    [Fact]
    public async Task WorkspaceLints_WhereTheTimeGoes()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await MeasureLintsAsync(
                GameProfile.BlackOps3,
                CorpusFixture.RawRoot!,
                CorpusFixture.Resolver,
                CorpusFixture.Scripts,
                (path, resolver, names) => CorpusFixture.Analyze(path, resolver, names));

            measured = true;
        }

        GameCorpus? cod4 = GameCorpusFixture.For(GameProfile.Cod4);
        if ( cod4 is not null )
        {
            GameCorpus captured = cod4;
            await MeasureLintsAsync(
                captured.Profile,
                captured.RawRoot,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured),
                (path, resolver, names) => GameCorpusFixture.Analyze(captured, path, resolver, names));

            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: neither %GSCODE_CORPUS_BO3% nor %GSCODE_CORPUS_COD4% found.");
        }
    }

    private async Task MeasureLintsAsync(
        GameProfile profile,
        string rawRoot,
        Func<PathResolver> resolverFactory,
        Func<IReadOnlyList<string>> scriptsFactory,
        Func<string, PathResolver, NameTable, ParseResult> analyse)
    {
        // The active profile has to move with the measurement, for the same reason the diagnostic
        // sweeps move it: the indexer enumerates through Active.ScriptGlobs and several lints fall
        // back to it. GameProfileCollection is what stops this racing another class.
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();

            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);

            List<PerfReport.Item> timings = [];

            foreach ( string path in scriptsFactory() )
            {
                ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

                try
                {
                    ParseResult parsed = analyse(path, resolver, names);

                    // Warm, then measure. Crashes are the lex/parse gate's business, not this one's,
                    // so a file that throws is dropped rather than counted as fast.
                    WorkspaceLints.LintsOnly(parsed, language, path, database, resolver, builtins, objectFields);

                    // AFTER the warm pass, so the scopes belong to the timed run. Reset-then-snapshot
                    // turns the global aggregate into a per-file profile, and is only sound because
                    // this sweep is sequential. Empty unless built with GSCODE_INSTRUMENTATION.
                    PerfTracker.Reset();

                    Stopwatch watch = Stopwatch.StartNew();
                    WorkspaceLints.LintsOnly(parsed, language, path, database, resolver, builtins, objectFields);
                    watch.Stop();

                    Dictionary<string, (double Milliseconds, long Count)> scopes = [];
                    PerfTracker.Snapshot(scopes);

                    timings.Add(new PerfReport.Item(
                        path, watch.Elapsed.TotalMilliseconds, new FileInfo(path).Length,
                        SubPhases: scopes.Count > 0 ? scopes : null));
                }
                catch ( Exception )
                {
                    continue;
                }
            }

            Report(profile.ShortName + "-lints", timings, rawRoot, cachedHeaders: 0);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// How many CALL-SITE completion requests to time per file — one file-scope request is timed
    /// besides, so a file contributes up to eleven. Ten rather than every call site, because this
    /// sweep pays a full index per game before it starts and a completion is far more expensive than
    /// a lint: BO3's 980 files at every call site would be tens of thousands of requests.
    ///
    /// They are taken EVENLY SPACED through the file's call sites rather than as the first ten. The
    /// first ten are all near the top, which on a real script is inside one or two functions, and the
    /// enclosing function decides which completion arm runs.
    /// </summary>
    private const int CompletionSamplesPerFile = 10;

    /// <summary>
    /// Where COMPLETION time goes — the interactive path with the least headroom and, until now, the
    /// only one nothing measured.
    ///
    /// The three sweeps above cover the parse, the cold index and the cross-file lints. Completion is
    /// none of those, and it is the one that cannot hide behind a debounce: <c>TextSyncHandler</c>
    /// holds diagnostics back ~250 ms, but <c>CompletionHandler</c> answers the keystroke that asked.
    ///
    /// Read the DISTRIBUTION, not the total. The question this exists to answer is whether a single
    /// request completes before the user types the next character, which is a p99 question; a sum
    /// over ten thousand requests answers nothing anybody experiences.
    ///
    /// The per-kilobyte column in the report is MEANINGLESS here and should be ignored — it is the
    /// one place this sweep's shape differs from the others. A parse costs what the file costs, so
    /// ms/KB finds superlinear parsing. A completion costs what the WORKSPACE costs: the queries
    /// behind it walk the record store, so a one-line file in a large workspace is as expensive as a
    /// long one. That is the property being measured.
    ///
    /// Needs a finished index for the same reason the lint sweep does, and more so: every query on
    /// this path reads the store, so against a partial index they return little and time nothing.
    ///
    /// CoD4 and BO3 only, per the two dialect families — and here the split is the point rather than
    /// a saving. A namespace dialect reaches <c>FunctionsInNamespace</c> once per imported namespace;
    /// a merge dialect reaches <c>FunctionsInIncludeScope</c> once. They are different code paths
    /// with different costs, and one game cannot stand in for the other.
    ///
    /// Reporting, not asserting, like the rest of this class.
    /// </summary>
    [Fact]
    public async Task Completion_WhereTheTimeGoes()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await MeasureCompletionAsync(
                GameProfile.BlackOps3,
                CorpusFixture.RawRoot!,
                CorpusFixture.Resolver,
                CorpusFixture.Scripts,
                (path, resolver, names) => CorpusFixture.Analyze(path, resolver, names));

            measured = true;
        }

        // CoD4 for the merge dialect, and BO1 for SIZE — the exception to the two-corpora rule, taken
        // for the same reason ColdIndex_WhereTheTimeGoes takes it. Every query on this path reads the
        // record store, so the quantity under test scales with the STORE rather than with the file,
        // and BO1's 2,963 scripts are the only corpus large enough to show that. Without it this
        // sweep can only say completion is fast on a thousand files, which is not the claim anybody
        // needs — a mod workspace is bigger than a stock one.
        foreach ( GameProfile profile in new[] { GameProfile.Cod4, GameProfile.BlackOps } )
        {
            GameCorpus? corpus = GameCorpusFixture.For(profile);
            if ( corpus is null )
            {
                continue;
            }

            GameCorpus captured = corpus;
            await MeasureCompletionAsync(
                captured.Profile,
                captured.RawRoot,
                () => GameCorpusFixture.Resolver(captured),
                () => GameCorpusFixture.Scripts(captured),
                (path, resolver, names) => GameCorpusFixture.Analyze(captured, path, resolver, names));

            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_BO3%, %GSCODE_CORPUS_COD4% or %GSCODE_CORPUS_BO1% found.");
        }
    }

    private async Task MeasureCompletionAsync(
        GameProfile profile,
        string rawRoot,
        Func<PathResolver> resolverFactory,
        Func<IReadOnlyList<string>> scriptsFactory,
        Func<string, PathResolver, NameTable, ParseResult> analyse)
    {
        // Same reason as MeasureLintsAsync: the indexer enumerates through Active.ScriptGlobs and the
        // completion engine defaults its dialect to Active. GameProfileCollection stops this racing
        // another class.
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();

            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(apiDirectory);
            ObjectFields objectFields = ObjectFields.Load(apiDirectory);
            CompletionEngine engine = new(database, builtins, objectFields);

            List<PerfReport.Item> timings = [];
            List<int> entryCounts = [];

            foreach ( string path in scriptsFactory() )
            {
                try
                {
                    ParseResult parsed = analyse(path, resolver, names);
                    string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));
                    long bytes = new FileInfo(path).Length;

                    foreach ( Position position in CompletionSamplePositions(parsed) )
                    {
                        // Warm, then measure — matching the other sweeps. The first completion in a
                        // file also pays for anything the parse result builds lazily, which belongs
                        // to neither this measurement nor the next file's.
                        entryCounts.Add(Complete(engine, parsed, contextId, position, profile));

                        PerfTracker.Reset();

                        Stopwatch watch = Stopwatch.StartNew();
                        Complete(engine, parsed, contextId, position, profile);
                        watch.Stop();

                        Dictionary<string, (double Milliseconds, long Count)> scopes = [];
                        PerfTracker.Snapshot(scopes);

                        // One item per REQUEST, not per file: the distribution being read is over
                        // keystrokes, and folding ten requests into one row would report a number
                        // no user ever waits for.
                        timings.Add(new PerfReport.Item(
                            path, watch.Elapsed.TotalMilliseconds, bytes,
                            SubPhases: scopes.Count > 0 ? scopes : null));
                    }
                }
                catch ( Exception )
                {
                    // Crashes are the lex/parse gate's business, not this one's.
                    continue;
                }
            }

            Report(profile.ShortName + "-completion", timings, rawRoot, cachedHeaders: 0);
            ReportEntryCounts(entryCounts);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// Completes with the SETTING DEFAULTS rather than the method defaults, so the sweep measures
    /// what a stock install actually runs. The dialect is passed explicitly for the reason
    /// <c>CompletionEngine.Complete</c> documents — a measurement that reads Active is at the mercy
    /// of whatever else touched it.
    /// </summary>
    /// <returns>How many entries came back — see <see cref="ReportEntryCounts"/> for why that matters.</returns>
    private static int Complete(
        CompletionEngine engine, ParseResult parsed, string contextId, Position position, GameProfile profile)
    {
        return engine.Complete(
            parsed,
            contextId,
            position,
            includeLiterals: true,
            fieldScope: FieldScope.Owner,
            callPunctuation: CallPunctuation.Parens,
            profile: profile,
            parameterHints: true).Length;
    }

    /// <summary>
    /// How big the returned lists were — which is how to tell whether the timings above mean
    /// anything.
    ///
    /// <c>Complete</c> has around ten arms and most of them are cheap and return almost nothing: a
    /// path segment list, an asset type list, an empty result where the position turned out not to be
    /// a completion site at all. Only the statement-scope arm reaches the store queries, and it
    /// returns thousands of entries in a real workspace.
    ///
    /// So a median time is only evidence about the expensive path if the median REQUEST took it. A
    /// sweep whose sample quietly landed on cheap arms reports fast completions and has measured
    /// nothing — which is the same failure mode PERF.md records for the lint sweep against a partial
    /// index, arriving by a different route.
    /// </summary>
    private void ReportEntryCounts(List<int> counts)
    {
        if ( counts.Count == 0 )
        {
            return;
        }

        List<int> sorted = [.. counts.Order()];
        int large = counts.Count(static c => c > 500);

        _output.WriteLine(
            $"    entries returned: median {Percentile([.. sorted.Select(static c => (double)c)], 0.50):F0} | "
            + $"p90 {Percentile([.. sorted.Select(static c => (double)c)], 0.90):F0} | max {sorted[^1]:N0}");
        _output.WriteLine(
            $"    {large:N0} of {counts.Count:N0} requests ({large * 100.0 / counts.Count:F1}%) returned over 500 entries "
            + "- those are the statement-scope arm, the one that queries the store");
    }

    /// <summary>
    /// The positions to complete at: the end of each call's NAME, which is exactly where the editor
    /// asks. A user typing <c>flag_wa|</c> has a partial identifier in statement position, the parser
    /// has read it as a call, and the engine takes its statement-scope arm — the arm that reaches the
    /// store queries this sweep exists to time.
    ///
    /// Read from the extraction's call references rather than found by scanning tokens, so the sample
    /// is real call sites in real files and needs no judgement about what counts as one.
    /// </summary>
    private static List<Position> CompletionSamplePositions(ParseResult parsed)
    {
        // The first sample is FILE SCOPE — the top of the file, outside every declaration. A call
        // reference can only ever be inside a function body, so a sweep built from them alone timed
        // one of the two arms: file scope used to return a static word list before any store query
        // ran, and now runs the same queries a body does.
        List<Position> sampled = [new Position(0, 0)];

        List<Position> calls = [];
        foreach ( ReferenceEntry entry in parsed.Extraction.References )
        {
            if ( entry.Kind == ReferenceKind.Call )
            {
                calls.Add(entry.Range.End);
            }
        }

        if ( calls.Count <= CompletionSamplesPerFile )
        {
            sampled.AddRange(calls);
            return sampled;
        }

        // Evenly spaced, so the sample spans the file rather than clustering in its first function.
        for ( int i = 0; i < CompletionSamplesPerFile; i++ )
        {
            sampled.Add(calls[i * calls.Count / CompletionSamplesPerFile]);
        }

        return sampled;
    }

    private static PerfReport.Item Time(
        string path, GameProfile game, IInsertProvider inserts, NameTable names,
        IHeaderMacroCache headerCache, Func<ParseResult> analyse)
    {
        long bytes = new FileInfo(path).Length;

        // Once to warm the file cache and any lazily built table, so the measurement is of analysis
        // rather than of first-touch I/O.
        analyse();

        // The four phases, timed individually — and their SUM is the total. There used to be a
        // separate stopwatch around a second `analyse()`, and the two measurements contradicted each
        // other badly: `_seaknight.gsc` reported 13.0 ms total against 0.2 ms of phases (65x), while
        // `pby_fly.gsc` reported 64.0 ms total against 74.1 ms of phases. Small files were dominated
        // by whatever GC pause happened to land in their window, which put them at the top of the
        // "slowest per kilobyte" table — the one place the report claims to find superlinear code.
        // So that table was ranking noise, and the files it named had no measurable analysis cost.
        //
        // Deriving the total from the phases makes the two agree by construction, and drops a whole
        // extra analysis pass per file. It does NOT make a single-shot measurement precise: at a
        // median near 0.1 ms one collection still swamps one file. Read a single row as indicative
        // and the distribution as real.
        //
        // Per PHASE rather than per function, because only two of the four are per-function work at
        // all: the lexer and the preprocessor walk the file once, whatever it declares. A file slow
        // in `preprocess` is slow because of what it INSERTS; slow in `parse` because of its size or
        // shape; slow in `extract` because of how much it declares.
        SourceText text = SourceText.From(File.ReadAllText(path));
        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(path);

        // AFTER the warm-up, so the scopes this file reports are the ones the timed phases opened
        // rather than the warm pass's. Reset-then-snapshot is what turns a global aggregate into a
        // per-file profile; it is only sound because this sweep is sequential.
        PerfTracker.Reset();

        Stopwatch phase = Stopwatch.StartNew();
        LexResult lexed = Lexer.Lex(text, game);
        double lex = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        PreprocessResult preprocessed = Preprocessor.Process(path, lexed.Tokens, text, inserts, names, game, headerCache);
        double preprocess = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        ParseTree tree = GSCode.Parser.Syntax.Parser.Parse(preprocessed.Tokens, game);
        double parse = phase.Elapsed.TotalMilliseconds;

        phase.Restart();
        SymbolExtractor.Extract(path, tree, preprocessed, lexed.Tokens, text, names, game);
        double extract = phase.Elapsed.TotalMilliseconds;

        // Stays empty in an ordinary build: the Snapshot call is [Conditional] and is not compiled
        // in at all, so this is the uninstrumented case the report is written to expect.
        Dictionary<string, (double Milliseconds, long Count)> scopes = new(StringComparer.Ordinal);
        PerfTracker.Snapshot(scopes);

        return new PerfReport.Item(
            path, lex + preprocess + parse + extract, bytes, lex, preprocess, parse, extract, scopes);
    }

    private void Report(string game, List<PerfReport.Item> timings, string root, int cachedHeaders)
    {
        if ( timings.Count == 0 )
        {
            return;
        }

        List<double> sorted = [.. timings.Select(static t => t.Milliseconds).Order()];
        double total = sorted.Sum();

        _output.WriteLine($"=== {game}: {timings.Count} files, {total:F0} ms total ===");
        _output.WriteLine(
            $"    median {Percentile(sorted, 0.50):F2} ms | p90 {Percentile(sorted, 0.90):F2} ms | "
            + $"p99 {Percentile(sorted, 0.99):F2} ms | max {sorted[^1]:F2} ms");

        // The tail's share of the total is the number that decides whether optimising outliers is
        // worth anything: a p99 ten times the median matters only if those files are a real slice.
        double tail = timings.OrderByDescending(static t => t.Milliseconds).Take(timings.Count / 100 + 1)
            .Sum(static t => t.Milliseconds);
        _output.WriteLine($"    slowest 1% account for {tail / total * 100:F1}% of the total");

        _output.WriteLine("    --- slowest by absolute time ---");
        foreach ( PerfReport.Item timing in timings.OrderByDescending(static t => t.Milliseconds).Take(10) )
        {
            _output.WriteLine(
                $"    {timing.Milliseconds,8:F1} ms  {timing.Bytes / 1024.0,7:F0} KB  "
                + $"{timing.MillisecondsPerKilobyte,6:F2} ms/KB  {Path.GetFileName(timing.Path)}");
        }

        // Slow FOR ITS SIZE is the interesting list: it is where an algorithm is superlinear rather
        // than where a file is simply long.
        _output.WriteLine("    --- slowest per kilobyte (files over 4 KB) ---");
        foreach ( PerfReport.Item timing in timings
            .Where(static t => t.Bytes > 4096)
            .OrderByDescending(static t => t.MillisecondsPerKilobyte)
            .Take(10) )
        {
            _output.WriteLine(
                $"    {timing.Milliseconds,8:F1} ms  {timing.Bytes / 1024.0,7:F0} KB  "
                + $"{timing.MillisecondsPerKilobyte,6:F2} ms/KB  {Path.GetFileName(timing.Path)}");
        }

        // Per world: a .gsc and a .csc are separate universes to the database, and one total
        // hides which of the two the time went to. Headers are counted separately again - they are
        // inserted rather than indexed, so they belong to neither.
        Dictionary<string, int> worlds = new(StringComparer.Ordinal)
        {
            ["gsc (server)"] = timings.Count(static t => t.Path.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase)),
            ["csc (client)"] = timings.Count(static t => t.Path.EndsWith(".csc", StringComparison.OrdinalIgnoreCase)),
            ["gsh (headers)"] = timings.Count(static t => t.Path.EndsWith(".gsh", StringComparison.OrdinalIgnoreCase)),
        };

        foreach ( KeyValuePair<string, int> world in worlds )
        {
            _output.WriteLine($"    {world.Key,-16} {world.Value}");
        }

        double lex = timings.Sum(static t => t.Lex);
        double pre = timings.Sum(static t => t.Preprocess);
        double par = timings.Sum(static t => t.Parse);
        double ext = timings.Sum(static t => t.Extract);
        double phases = lex + pre + par + ext;
        if ( phases > 0 )
        {
            _output.WriteLine(
                $"    phases: lex {lex / phases * 100:F0}% | preprocess {pre / phases * 100:F0}% | "
                + $"parse {par / phases * 100:F0}% | extract {ext / phases * 100:F0}%");
        }

        IReadOnlyList<(string Name, double Milliseconds, long Count)> subPhases = PerfReport.SubPhaseTotals(timings);
        if ( subPhases.Count == 0 )
        {
            _output.WriteLine("    sub-phases: not instrumented (rebuild with -p:GscodeInstrumentation=true)");
        }
        else
        {
            foreach ( (string name, double milliseconds, long count) in subPhases )
            {
                double mean = count == 0 ? 0 : milliseconds / count;
                _output.WriteLine($"    {name,-24} {milliseconds,8:F0} ms  {count,8:N0} calls  {mean:F4} ms mean");
            }
        }

        PerfReport.Memory memory = PerfReport.Sample();
        _output.WriteLine(
            $"    memory: live {memory.ManagedLive / 1048576.0:F0} MB | heap {memory.HeapSize / 1048576.0:F0} MB | "
            + $"fragmented {memory.Fragmented / 1048576.0:F0} MB | working set {memory.WorkingSet / 1048576.0:F0} MB");

        WriteReport(game, timings, root, worlds, memory, cachedHeaders);
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>
    /// Writes the page beside the diagnostic sweep's, under a DIFFERENT name so a perf run never
    /// overwrites a diagnostic report someone is still reading. <c>GSCODE_PERF_REPORT</c> overrides
    /// the directory, matching GSCODE_SWEEP_REPORT.
    /// </summary>
    private void WriteReport(
        string game, IReadOnlyList<PerfReport.Item> timings, string root,
        IReadOnlyDictionary<string, int> worlds, PerfReport.Memory memory, int cachedHeaders)
    {
        string directory = Environment.GetEnvironmentVariable("GSCODE_PERF_REPORT") is string configured
            && configured.Length > 0
                ? configured
                : ScratchDirectory();

        string path = Path.Combine(directory, $"gscode-perf-{game}.html");
        PerfReport.Write(path, game, timings, root, worlds, memory, cachedHeaders);
        _output.WriteLine($"Report [{game}]: {path}");
    }

    /// <summary>
    /// The repository's <c>temp/</c> folder, found by walking up to the <c>.git</c> entry — which is
    /// a directory in a clone and a FILE in a worktree, so both are checked. Falls back to the system
    /// temp folder when there is no repository above, as in a packaged run.
    /// </summary>
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
