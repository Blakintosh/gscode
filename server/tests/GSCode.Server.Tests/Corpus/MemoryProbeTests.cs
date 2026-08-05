using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Corpus;

/// <summary>
/// What a full workspace index RETAINS, per game, watched for fifteen seconds afterwards.
///
/// Written to bisect a reported memory regression, so it is deliberately built to be carried
/// unchanged onto older commits (`git checkout &lt;commit&gt; -- .` then restore this one file): it
/// touches only `WorkspaceIndexer`, `ScriptDatabase` and `PathResolver`, whose shapes have been
/// stable across the range under suspicion. Nothing here references anything added recently.
///
/// Two things it measures that a single post-index sample cannot:
///
/// * <b>Retention, not peak.</b> The database is held live for the whole watch, so what is reported
///   is what the workspace costs to KEEP, which is the number a long-running server lives with.
/// * <b>Settling.</b> The reported spike appears shortly AFTER compaction, so one sample cannot tell
///   a genuine retention increase from a heap that has not finished coming down. Fifteen one-second
///   samples separate the two.
///
/// Every sample forces a full blocking collection first (see <c>PerfReport.Sample</c>), so this is
/// retained memory rather than uncollected garbage — and that also means the numbers here are NOT
/// comparable with the server's own log lines, which sample without forcing.
/// </summary>
[Trait("Category", "Perf")]
[Collection(GameProfileCollection.Name)]
public class MemoryProbeTests
{
    private const int WatchSeconds = 15;

    private readonly ITestOutputHelper _output;

    public MemoryProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// The same watch, but with the SQLite cache ATTACHED — which is the configuration a real server
    /// runs and the one the probe above cannot see.
    ///
    /// Reported symptom: memory falls after the post-index compaction and then climbs again. The
    /// compaction happens before the cache writer has finished, and that writer is a single thread
    /// that serializes and gzips every record it drains — <c>RecordSerializer.Serialize</c> makes
    /// three full copies of each (JSON bytes, the gzip buffer, then <c>ToArray</c>), and a record
    /// carrying a whole file's references and diagnostics is large enough for those to land on the
    /// large-object heap.
    ///
    /// So the climb would be the drain, not a leak, and it would be invisible to any measurement
    /// taken without a cache. This exists to prove or disprove that.
    /// </summary>
    [Fact]
    public async Task EachGame_IndexWithCache_ThenWatchTheDrain()
    {
        if ( !GameCorpusFixture.Available().Any() && !CorpusFixture.Available )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_<GAME>% found.");
            return;
        }

        foreach ( GameCorpus corpus in GameCorpusFixture.Available() )
        {
            GameCorpus captured = corpus;
            await ProbeWithCacheAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
        }
    }

    private async Task ProbeWithCacheAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        string databasePath = Path.Combine(Path.GetTempPath(), $"gscode-probe-{Guid.NewGuid():N}.db");

        try
        {
            GameProfile.Select(profile.ShortName);

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);

            await using SqliteCache cache = SqliteCache.Open(databasePath, "probe-identity");
            indexer.UseCache(cache, cache.LoadAll());

            Stopwatch watch = Stopwatch.StartNew();
            IndexOutcome outcome = await indexer.IndexAsync(
                IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
            watch.Stop();

            _output.WriteLine("");
            _output.WriteLine(
                $"########## {profile.ShortName} WITH CACHE: {outcome.Total} files in {watch.Elapsed.TotalMilliseconds:F0} ms");
            _output.WriteLine("        t   live MB   heap MB   frag MB   workset MB   gen2");

            // Sampled straight after IndexAsync returns, which is exactly where the server compacts —
            // and the writer is still draining behind it.
            // UNFORCED, unlike every other sample in this file, and that is the entire point. The
            // reported symptom is working set climbing after the post-index compaction, and a probe
            // that forces a blocking collection every second erases the thing it is looking for.
            // This watches what the OS would report — which is what a user sees.
            for ( int second = 0; second <= WatchSeconds; second++ )
            {
                GCMemoryInfo live = GC.GetGCMemoryInfo();
                _output.WriteLine(
                    $"     {second,4}s {Mb(GC.GetTotalMemory(forceFullCollection: false)),9} "
                    + $"{Mb(live.HeapSizeBytes),9} {Mb(live.FragmentedBytes),9} "
                    + $"{Mb(Environment.WorkingSet),12} {GC.CollectionCount(2),6}");

                if ( second < WatchSeconds )
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            // Then once WITH a collection, so the two are side by side: whatever the gap is between
            // the last unforced row and this one is garbage and holes, not retention.
            (long Live, long Heap, long Fragmented, long WorkingSet, int Gen2) settled = Sample();
            _output.WriteLine(
                $"     forced   {Mb(settled.Live),9} {Mb(settled.Heap),9} {Mb(settled.Fragmented),9} "
                + $"{Mb(settled.WorkingSet),12} {settled.Gen2,6}");

            _output.WriteLine($"     dropped writes: {cache.DroppedWrites}");
            GC.KeepAlive(indexer);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
            try
            {
                File.Delete(databasePath);
            }
            catch ( IOException )
            {
            }
        }
    }

    [Fact]
    public async Task EachGame_IndexThenWatchRetainedMemory()
    {
        bool measured = false;

        if ( CorpusFixture.Available )
        {
            await ProbeAsync(GameProfile.BlackOps3, CorpusFixture.Resolver);
            measured = true;
        }

        foreach ( GameCorpus corpus in GameCorpusFixture.Available() )
        {
            GameCorpus captured = corpus;
            await ProbeAsync(captured.Profile, () => GameCorpusFixture.Resolver(captured));
            measured = true;
        }

        if ( !measured )
        {
            _output.WriteLine("SKIPPED: no %GSCODE_CORPUS_<GAME>% found.");
        }
    }

    private async Task ProbeAsync(GameProfile profile, Func<PathResolver> resolverFactory)
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select(profile.ShortName);

            // Baseline BEFORE anything is built, so each game's figures are its own rather than the
            // previous game's leftovers. The database from the last probe is unreachable by now.
            (long Live, long Heap, long Fragmented, long WorkingSet, int Gen2) before = Sample();

            PathResolver resolver = resolverFactory();
            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, new PhysicalFileSystem(), names);

            Stopwatch watch = Stopwatch.StartNew();
            IndexOutcome outcome = await indexer.IndexAsync(
                IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
            watch.Stop();

            _output.WriteLine("");
            _output.WriteLine(
                $"########## {profile.ShortName}: {outcome.Total} files ({outcome.Analysed} analysed) "
                + $"in {watch.Elapsed.TotalMilliseconds:F0} ms");
            _output.WriteLine($"     baseline before index: live {Mb(before.Live)} MB");
            _output.WriteLine("        t   live MB   heap MB   frag MB   workset MB   gen2");

            for ( int second = 0; second <= WatchSeconds; second++ )
            {
                (long Live, long Heap, long Fragmented, long WorkingSet, int Gen2) sample = Sample();
                _output.WriteLine(
                    $"     {second,4}s {Mb(sample.Live),9} {Mb(sample.Heap),9} "
                    + $"{Mb(sample.Fragmented),9} {Mb(sample.WorkingSet),12} {sample.Gen2,6}");

                if ( second < WatchSeconds )
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }

            // The whole point of the watch: the database has to stay reachable, or the samples
            // describe an empty heap. This is what forbids the JIT from collecting it early.
            _output.WriteLine(
                $"     retained: {database.Gsc.Count} gsc + {database.Csc.Count} csc records still live");

            ReportComposition(database);

            GC.KeepAlive(indexer);
            GC.KeepAlive(names);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// WHAT the records hold, not just how much they cost.
    ///
    /// A per-game total says BO1 is expensive; it does not say which part of a record is. These
    /// counts do, and they are the thing to compare ACROSS games — a category that is
    /// disproportionate per file is the one worth attacking, and one that merely scales with file
    /// count is not a defect.
    ///
    /// Diagnostic messages are counted separately in characters because they are the one category
    /// that carries a freshly formatted STRING per entry, rather than references into text that
    /// already exists.
    /// </summary>
    private void ReportComposition(ScriptDatabase database)
    {
        long records = 0, references = 0, diagnostics = 0, messageChars = 0;
        long functions = 0, classes = 0, macros = 0, dependencies = 0, pathCalls = 0;

        foreach ( ScriptRecord record in database.AllRecords )
        {
            records++;
            references += record.References.Length;
            diagnostics += record.Diagnostics.Length;
            functions += record.Functions.Length;
            classes += record.Classes.Length;
            macros += record.Macros.Length;
            dependencies += record.Dependencies.Length;
            pathCalls += record.PathCallTargets.Length;

            foreach ( GSCode.Core.Diagnostics.Diagnostic diagnostic in record.Diagnostics )
            {
                messageChars += diagnostic.Message?.Length ?? 0;
            }
        }

        double perFile = records == 0 ? 0 : 1.0 / records;
        _output.WriteLine(
            $"     composition over {records:N0} records (per file in brackets):");
        _output.WriteLine(
            $"       references  {references,9:N0} ({references * perFile,7:F1})   "
            + $"diagnostics {diagnostics,7:N0} ({diagnostics * perFile,6:F2})   "
            + $"msg chars {messageChars,9:N0}");
        _output.WriteLine(
            $"       functions   {functions,9:N0} ({functions * perFile,7:F1})   "
            + $"macros      {macros,7:N0}   classes {classes,6:N0}   "
            + $"deps {dependencies,6:N0}   pathcalls {pathCalls,7:N0}");
    }

    /// <summary>
    /// A memory sample, taken AFTER a forced blocking collection so it reports what is RETAINED
    /// rather than what happens to be uncollected.
    ///
    /// Deliberately not <c>PerfReport.Sample</c>, though it does the same thing: this probe has to
    /// compile on commits predating that helper, and a bisect is worthless if the instrument only
    /// builds on one side of it.
    /// </summary>
    private static (long Live, long Heap, long Fragmented, long WorkingSet, int Gen2) Sample()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        GCMemoryInfo info = GC.GetGCMemoryInfo();
        return (
            GC.GetTotalMemory(forceFullCollection: false),
            info.HeapSizeBytes,
            info.FragmentedBytes,
            Environment.WorkingSet,
            GC.CollectionCount(2));
    }

    private static string Mb(long bytes)
    {
        return (bytes / 1048576.0).ToString("F1");
    }
}
