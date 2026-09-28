using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Logging;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;

namespace GSCode.Server.Startup;

/// <summary>
/// The <c>OnStarted</c> work: which mode to index in, opening the workspace cache, and — if
/// indexing is not off — the whole startup indexing task (index, full-mode lint sweep, diagnostics
/// refresh, compaction, and the transition into steady-state memory sampling).
///
/// Pulled out of <c>Program.cs</c>'s top-level statements, which cannot be unit tested at all —
/// this class can be constructed with its dependencies and driven directly. Takes an
/// <see cref="IServiceProvider"/> and an <see cref="ILanguageServerFacade"/> rather than whatever
/// type OmniSharp's <c>OnStarted</c> hands the caller, so it depends on two ordinary,
/// already-used-elsewhere types instead of a third one only this call site would need to name.
/// </summary>
internal sealed class StartupIndexRunner
{
    private readonly ServerSettings _settings;
    private readonly ResolverHolder _resolverHolder;
    private readonly CacheHolder _cacheHolder;
    private readonly IndexingLifetime _indexingLifetime;
    private readonly ConnectionSettleGate _settleGate;
    private readonly StartupLogController _logController;

    public StartupIndexRunner(
        ServerSettings settings,
        ResolverHolder resolverHolder,
        CacheHolder cacheHolder,
        IndexingLifetime indexingLifetime,
        ConnectionSettleGate settleGate,
        StartupLogController logController)
    {
        _settings = settings;
        _resolverHolder = resolverHolder;
        _cacheHolder = cacheHolder;
        _indexingLifetime = indexingLifetime;
        _settleGate = settleGate;
        _logController = logController;
    }

    public Task RunAsync(IServiceProvider services, ILanguageServerFacade facade, CancellationToken cancellationToken)
    {
        // Which game is actually parsing, sent as soon as the connection can carry it. The
        // status bar shows this permanently, so it must not depend on indexing: with
        // workspaceIndexingMode=off nothing below this line runs, and a label that arrived only
        // with gscode/indexingComplete would never appear for those users at all.
        //
        // It is also the server's own answer rather than the client's gscode.game setting. An
        // unrecognised name falls back to BO3, so the setting says what was asked for while this
        // says what was selected — and a status bar confirming a game that is not in use is
        // worse than none, because it rules out the very thing that is wrong.
        _settleGate.SendOnceSettled(() => facade.SendNotification(
            "gscode/serverReady",
            new ServerReadyParams(GameProfile.Active.Abbreviation, GameProfile.Active.DisplayName)));

        // Kick off cold-start indexing only once the server is fully started — the
        // client connection is ready to receive gscode/indexing* notifications now
        // (sending them during OnInitialized drops them). Editor traffic is unaffected.
        IndexingMode mode = _settings.WorkspaceIndexingMode.ToLowerInvariant() switch
        {
            "off" => IndexingMode.Off,
            "full" => IndexingMode.Full,
            _ => IndexingMode.Partial,
        };

        WorkspaceIndexer indexer = services.GetRequiredService<WorkspaceIndexer>();

        // Opened regardless of indexing mode, not only inside the block below: a user running
        // with workspaceIndexingMode=off still has a cache from a PREVIOUS session on disk,
        // and gscode/clearCache used to answer "No workspace cache is open" for them — true
        // of the indexer's own use of it, misleading about whether one exists to clear.
        //
        // Timed, because this is where a warm start used to disappear. `LoadAll` is an
        // ARGUMENT to UseCache, so it ran to completion before the stopwatch below was even
        // started, and every warm figure on record was the index alone. It was reading and
        // deserializing every cached record on this one thread while the parallel index it
        // was feeding sat idle behind it — 1,509 ms on BO3 in front of a cold index that
        // does the whole job in 390. Now it reads blobs only and the deserialize happens on
        // the indexing threads, but the number stays in the log either way: an untimed
        // stage is one that can regress without anybody noticing.
        TimeSpan restoreElapsed = TimeSpan.Zero;
        if ( _settings.EnableWorkspaceCache )
        {
            System.Diagnostics.Stopwatch restoreWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SqliteCache.CleanUpLegacyCache();
                RootConfig roots = _resolverHolder.Current.Config;
                List<string> cacheKeyRoots = [.. roots.WorkspaceFolders];
                if ( roots.RawRoot is not null )
                {
                    cacheKeyRoots.Add(roots.RawRoot);
                }

                if ( roots.ModsRoot is not null )
                {
                    cacheKeyRoots.Add(roots.ModsRoot);
                }

                string databasePath = SqliteCache.ResolveDatabasePath(cacheKeyRoots);
                string identity = ServerBuildIdentity.Compute(
                    IndexReporting.BundledDataFilePaths(), GameProfile.Active.ShortName);
                SqliteCache workspaceCache = SqliteCache.Open(databasePath, identity);
                _cacheHolder.Set(workspaceCache, databasePath);
                // Priming an indexer that workspaceIndexingMode=off never runs is harmless —
                // held state nobody reads — and simpler than threading "should I bother"
                // through this block for what is already a rare (cache disabled) path.
                indexer.UseCache(workspaceCache, workspaceCache.LoadAll());
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to open the workspace cache; continuing without it");
            }

            // Outside the catch, so a cache that failed to open still reports what the
            // attempt cost rather than reporting zero.
            restoreWatch.Stop();
            restoreElapsed = restoreWatch.Elapsed;
        }

        if ( mode == IndexingMode.Off )
        {
            // No indexing climb to sample around when indexing never runs, so nothing here
            // waits for one — the non-Off path starts this only once indexing finishes,
            // exactly to avoid sampling that climb; see the call site below.
            _ = services.GetRequiredService<ServerStatusNotifier>().RunAsync(_indexingLifetime.Token);

            // Startup is over the moment OnStarted returns when there is no index to run, so
            // this is where the channel drops to what the client asked for.
            _logController.Settle();
        }

        if ( mode != IndexingMode.Off )
        {
            IndexProgressNotifier notifier = new(facade);
            DocumentStore documents = services.GetRequiredService<DocumentStore>();

            Task indexingTask = Task.Run(async () =>
            {
                try
                {
                    // The WORK does not need to wait for the pipe to settle, only the
                    // notifications describing it do — so the notifier is handed the shared
                    // gate's clock instead of starting its own, and indexing runs through it
                    // immediately.
                    notifier.SendNothingBefore(_settleGate.Settled);

                    System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    IndexOutcome outcome = await indexer.IndexAsync(
                        mode, notifier, _indexingLifetime.Token,
                        ownedByEditor: documents.IsOpen);
                    stopwatch.Stop();
                    // Split, because "indexing took 2.8s" hid which half was slow and the two
                    // have nothing to do with each other. Enumeration is serial and depends on
                    // how big the WORKSPACE is; analysis is parallel and depends on how many
                    // SCRIPTS there are. A workspace folder holding a whole game install is
                    // 295,640 files to find 1,105 scripts, and it read as slow analysis.
                    //
                    // The parallelism figure is thread-time over analysis wall-clock. Well below
                    // the core count means the workers are not running — contention, or a lock —
                    // rather than each file being expensive.
                    Log.Information(
                        "Workspace indexing complete: {Count} files in {Seconds:F1}s "
                        + "(cache {Restore:F1}s, find {Enumerate:F1}s, analyse {Analyse:F1}s at {Parallelism:F1}x, "
                        + "{Restored:N0} from cache)",
                        outcome.Total,
                        restoreElapsed.TotalSeconds + stopwatch.Elapsed.TotalSeconds,
                        restoreElapsed.TotalSeconds,
                        outcome.Enumerate.TotalSeconds,
                        outcome.Analyse.TotalSeconds,
                        outcome.Parallelism,
                        outcome.Restored);

                    // What the user actually waited: the process has been up since before the
                    // client connected, and indexing is only the last part of it.
                    Log.Information(
                        "Ready {Seconds:F1}s after start",
                        (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds);
                    // A dropped cache write is not an error the user can act on, but it is the
                    // difference between the next start being warm and it silently re-analysing
                    // part of the workspace. It used to be invisible.
                    if ( _cacheHolder.Current is SqliteCache activeCache && activeCache.DroppedWrites > 0 )
                    {
                        Log.Warning(
                            "{Count} record(s) could not be queued for the workspace cache; those files "
                            + "will be re-analysed on the next start",
                            activeCache.DroppedWrites);
                    }

                    if ( outcome.SkippedOversized > 0 )
                    {
                        Log.Warning(
                            "{Count} file(s) skipped: larger than the {Limit} MB analysis limit",
                            outcome.SkippedOversized,
                            WorkspaceIndexer.MaxAnalysedCharacters / (1024 * 1024));
                    }

                    // `full` beyond `partial`: the cross-file lints, over every indexed
                    // GSC/CSC file rather than just open ones. Runs AFTER the index and BEFORE
                    // the diagnostics refresh below, so a closed file's Problems entry is
                    // upgraded before anything republishes it — see WorkspaceLintSweep and
                    // PERF.md's 2026-09-15 entry for why this is affordable where
                    // FOLLOWUPS.md's older estimate said it was not.
                    if ( mode == IndexingMode.Full )
                    {
                        System.Diagnostics.Stopwatch lintStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        LintSweepOutcome lintOutcome = await services
                            .GetRequiredService<WorkspaceLintSweep>()
                            .RunFullSweepAsync(_indexingLifetime.Token);
                        lintStopwatch.Stop();

                        Log.Information(
                            "Workspace lint sweep complete: {Linted} file(s) in {Seconds:F1}s ({Skipped} skipped)",
                            lintOutcome.Linted,
                            lintStopwatch.Elapsed.TotalSeconds,
                            lintOutcome.Skipped);
                    }

                    // Guarded: the breakdown walks every record in both stores plus every GSH,
                    // accumulating counts and a namespace set, to produce ONE Verbose line. That
                    // traversal ran whatever the log level was.
                    if ( Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose) )
                    {
                        IndexReporting.LogIndexBreakdown(services.GetRequiredService<ScriptDatabase>());
                    }

                    // Only now: the cross-file picture is complete, and a record's stored
                    // diagnostics are meaningless for files still waiting to be analysed.
                    services.GetRequiredService<WorkspaceDiagnosticsPublisher>().Refresh();

                    // That publisher deliberately skips OPEN documents, so on its own it leaves
                    // the one file the user is actually looking at stale. A tab restored with the
                    // window is opened during initialize, which is before this point, so its
                    // didOpen linted it against a half-built index — and the lints gated on
                    // HasCompletedIndex (5013/5014/5025/5026) stayed silent. The file looked clean
                    // until it was closed and reopened, which is why starting on a DIFFERENT file
                    // and switching to it appeared to fix the problem: that switch was the
                    // didOpen. Same reasoning as an on-disk change: the world moved under every
                    // open document and none of them owns the event, so all of them are
                    // dependents. Costs a lint pass each, not a re-parse. The no-origin call below
                    // is why the verbose log for this pass reads "after an on-disk change" even
                    // though nothing on disk moved — Schedule("") means "no single caller", and
                    // that is exactly this case too.
                    services.GetRequiredService<DependentDiagnosticsRefresher>().Schedule();

                    // Sampled before the monitor starts, so the number reflects the state
                    // indexing left behind rather than anything steady-state traffic did.
                    IndexReporting.LogMemoryReport("indexing", outcome);

                    // Let the cache writer finish FIRST. It is handed a blob per file and can
                    // still be writing after IndexAsync returns — compacting before it drains measures a heap that is
                    // about to be dirtied again, which is precisely the "drops, then climbs
                    // back" the memory report kept showing.
                    if ( _cacheHolder.Current is SqliteCache draining )
                    {
                        await draining.WaitForIdleAsync(_indexingLifetime.Token);
                    }

                    Compact();
                    IndexReporting.LogMemoryReport("compaction", outcome);

                    // Compiles to nothing without -p:GscodeInstrumentation=true, so a normal
                    // build pays neither the timing scopes nor this dump.
                    //
                    // Debug, not Information: an instrumented build reports thirty-odd scopes
                    // in one burst, and at Information they land in the same channel as the
                    // handful of lines that say what the server is DOING. Debug is a level
                    // ABOVE Verbose in Serilog's ordering, which is the point - the dump is
                    // what an instrumented build was made for, so it should not need the level
                    // that also turns on a line per file for a thousand files. Same reasoning
                    // as the slow-file line.
                    PerfTracker.Report(line => Log.Debug("Perf  {Scope}", line));

                    // Start sampling memory only now — during indexing it climbs steadily,
                    // and every sample would be a change. One sampler serves both the
                    // status-bar tooltip and the verbose log.
                    //
                    // Shares indexingLifetime's token rather than running forever
                    // uncancellably: it holds no resource that needs a clean drain, so there
                    // is nothing lost in letting shutdown stop it the same way it stops
                    // everything else in this task.
                    _ = services.GetRequiredService<ServerStatusNotifier>()
                        .RunAsync(_indexingLifetime.Token);
                }
                catch ( OperationCanceledException )
                {
                    // Shutdown, or gscode/clearCache, asked this to stop — not a failure, and
                    // not worth notifying the client about: the connection is going away (or
                    // about to reload) either way.
                }
                catch ( Exception exception )
                {
                    Log.Error(exception, "Workspace indexing failed");

                    // Terminal either way: gscode/indexingComplete is the one notification
                    // that may never simply be dropped, on pain of a status-bar spinner that
                    // runs for the rest of the session looking like a hang. A genuine failure
                    // gets its own notification instead of a fabricated completion, so the
                    // client can tell "nothing to index" from "something broke".
                    notifier.Failed(exception.Message);
                }
                finally
                {
                    // Startup ends here, however it ended: everything above this line is what a
                    // bug report is attached for, and everything after it is steady state.
                    _logController.Settle();
                }
            }, _indexingLifetime.Token);

            _indexingLifetime.SetTask(indexingTask);
        }

        return Task.CompletedTask;
    }

    // One compacting collection at the indexing -> serving transition, UNCONDITIONALLY.
    //
    // Calling GC.Collect is normally wrong, but this is the case that justifies it: a one-off phase
    // change after which the allocation profile is completely different. Analysing a file allocates
    // token and PToken arrays that clear the 85 KB large-object threshold, so most scripts put theirs
    // straight on the LOH — which is NOT compacted by default. Measured on 1,105 files: a cold index
    // left 183 MB fragmented out of a 282 MB heap (65% holes) even after 19 gen2 collections, because
    // ordinary collections reclaim LOH memory without moving anything. Serving requests allocates
    // nothing like that, so anything left behind would simply persist.
    //
    // It used to be gated on 32 MB of measured fragmentation, and that gate made sense while
    // fragmentation was the whole problem — a warm start had none and skipped the pause. It stopped
    // making sense once System.GC.ConserveMemory took fragmentation to roughly zero: the gate then read
    // "nothing to do" while the large-object heap was still holding tens of megabytes of committed,
    // unfragmented, unreturned space that no ordinary collection gives back.
    //
    // Fragmentation was never the thing worth measuring. What the user sees is committed memory, and
    // CompactOnce is the only thing that returns large-object pages to the OS. It runs once per index.
    //
    // Its cost is a one-off pause, and the pause is NOT confined to this thread — the comment here
    // said so for a while and it was wrong about who waits. Both collections below are
    // `blocking: true` gen2s, which suspend every thread in the process, and the LSP connection is
    // live by the time this runs (RunAsync returns as soon as the indexing task is launched). So a
    // request arriving in that window waits for it: 1.8 s at 50,000 files, once, at the end of
    // indexing (PERF.md, the scale section, which states this correctly).
    //
    // It still earns its place — it returned 446 MB on bo1, and the fragmentation gate that used to
    // suppress it was the bug. Deferring it to the first idle moment is a separate decision that
    // needs its own measurement, and is not made here.
    private static void Compact()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // The second pass collects what the finalizers just released; the LOH mode is one-shot and has
        // already been consumed, so this one is an ordinary compacting gen2.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
