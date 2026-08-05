using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Symbols;
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

            GC.KeepAlive(indexer);
            GC.KeepAlive(names);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
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
