using System.Diagnostics;
using GSCode.Workspace.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Payload for gscode/serverReady: which game the server actually selected, sent once the
/// connection is live and before any indexing decision, so the status bar can name it whatever
/// the indexing mode is.
/// </summary>
public sealed record ServerReadyParams(string Game, string GameName);

/// <summary>Payload for gscode/indexingStarted.</summary>
public sealed record IndexingStartedParams(int TotalFiles);

/// <summary>Payload for gscode/indexingProgress.</summary>
public sealed record IndexingProgressParams(int FilesIndexed, int TotalFiles);

/// <summary>
/// Payload for gscode/serverStatus: what the server is holding right now.
///
/// Sent on a change rather than a schedule, so an idle server produces no traffic at all. The
/// status-bar tooltip is otherwise frozen at whatever memory happened to be in use the instant
/// indexing finished, which is the least interesting moment to sample it.
/// </summary>
public sealed record ServerStatusParams(double WorkingSetMegabytes);

/// <summary>Payload for gscode/indexingComplete.</summary>
/// <param name="WorkingSetMegabytes">
/// What the server is holding, so the status-bar tooltip can show it. The number was previously
/// only reachable by turning on a log level and reading past everything else.
/// </param>
public sealed record IndexingCompleteParams(
    int FilesIndexed, int TotalFiles, long ElapsedMilliseconds, double WorkingSetMegabytes);

/// <summary>
/// Maps indexer progress onto the gscode/indexing* notifications. Progress fires on
/// every file completion but is coalesced to at most one notification per ~40 ms so
/// the status-bar counter visibly races without flooding the pipe; the final count
/// always sends. Concrete record payloads (not anonymous objects) keep OmniSharp's
/// Newtonsoft contract resolver happy.
/// </summary>
public sealed class IndexProgressNotifier : IIndexProgressListener
{
    private const long ThrottleMilliseconds = 40;

    private readonly ILanguageServerFacade _server;
    private readonly Stopwatch _sinceLastSend = Stopwatch.StartNew();

    /// <summary>
    /// Completes when the connection's output pump has settled enough for a notification to
    /// survive. Sending inside the initialize/initialized window drops them.
    ///
    /// Indexing used to wait on this before it began, which spent the settling time doing nothing.
    /// It is the NOTIFICATIONS that cannot go early, not the work, so the wait now lives here and
    /// the indexer starts immediately.
    /// </summary>
    private Task _settled = Task.CompletedTask;

    private readonly Lock _startGate = new();
    private int _startedTotal = -1;
    private bool _startedSent;

    public IndexProgressNotifier(ILanguageServerFacade server)
    {
        _server = server;
    }

    /// <summary>Holds every notification until <paramref name="settled"/> completes.</summary>
    public void SendNothingBefore(Task settled)
    {
        _settled = settled;
    }

    public void Started(int totalFiles)
    {
        // The server's own channel, not the client's. This line used to be written by the
        // extension host, which put the one message telling you indexing had begun in a different
        // output channel from every other thing the language server says — including whatever you
        // opened the channel to diagnose.
        Log.Information("Indexing {Count} script file(s)…", totalFiles);

        // Remembered rather than sent, because indexing now starts before the pipe is ready.
        // Whichever of Progressed or Completed first finds the window open sends it, so the client
        // still receives it in order and still receives it exactly once.
        Volatile.Write(ref _startedTotal, totalFiles);
        FlushStartedIfReady();
    }

    /// <summary>
    /// Sends the remembered "started" if the window is open and it has not gone out yet. True once
    /// the client has it, which is the caller's permission to send anything that assumes it.
    ///
    /// Locked rather than claimed with an Interlocked flag, and the difference matters: a thread
    /// that merely LOSES the claim would carry on and send its progress notification while the
    /// winner was still inside SendNotification, so the client could see a position report before
    /// it had been told the total. Holding the lock makes the loser wait for the send it skipped.
    /// Uncontended after the first one, and progress is throttled to 40 ms besides.
    /// </summary>
    private bool FlushStartedIfReady()
    {
        if ( !_settled.IsCompleted )
        {
            return false;
        }

        int totalFiles = Volatile.Read(ref _startedTotal);
        if ( totalFiles < 0 )
        {
            return false;
        }

        lock ( _startGate )
        {
            if ( _startedSent )
            {
                return true;
            }

            _server.SendNotification("gscode/indexingStarted", new IndexingStartedParams(totalFiles));
            _startedSent = true;
            return true;
        }
    }

    /// <summary>
    /// Per-file timing, at Verbose. There was previously nothing at all below Information, so
    /// `gscode.serverLogLevel: verbose` produced byte-identical output to `info` — a setting whose
    /// description promised detail and delivered none.
    ///
    /// Runs on the parallel indexing path, so it does no work when Verbose is off: Serilog's own
    /// level check short-circuits before the message template is rendered.
    /// </summary>
    public void FileIndexed(string path, TimeSpan elapsed, bool restoredFromCache)
    {
        Log.Verbose(
            "Indexed {Path} in {Elapsed:F1}ms ({Source})",
            path,
            elapsed.TotalMilliseconds,
            restoredFromCache ? "cache" : "analysed");

        // Repeated at Debug, a level ABOVE Verbose in Serilog's ordering, so a slow file stands
        // out in a log that now holds a line for every one of a thousand files.
        if ( !restoredFromCache && elapsed.TotalMilliseconds >= SlowFileMilliseconds )
        {
            Log.Debug("Slow file: {Path} took {Elapsed:F0}ms", path, elapsed.TotalMilliseconds);
        }
    }

    /// <summary>A file taking longer than this is worth a line of its own at Debug.</summary>
    private const int SlowFileMilliseconds = 250;

    public void Progressed(int filesIndexed, int totalFiles)
    {
        // Dropped outright while the window is closed — which is what happened to them before, the
        // difference being that the indexer was idle then and is working now. Progress is a
        // position report, so losing the early ones costs nothing; the next one carries the truth.
        if ( !_settled.IsCompleted )
        {
            return;
        }

        // No progress before the total it is a fraction of.
        if ( !FlushStartedIfReady() )
        {
            return;
        }

        bool isFinal = filesIndexed == totalFiles;
        if ( !isFinal && _sinceLastSend.ElapsedMilliseconds < ThrottleMilliseconds )
        {
            return;
        }

        _sinceLastSend.Restart();
        _server.SendNotification("gscode/indexingProgress", new IndexingProgressParams(filesIndexed, totalFiles));
    }

    public void Completed(int filesIndexed, int totalFiles, TimeSpan elapsed)
    {
        Log.Debug(
            "Indexing finished: {Count} file(s) in {Seconds:F1}s",
            filesIndexed,
            elapsed.TotalSeconds);

        // The one notification that may NOT be dropped: it is terminal, and a client that misses it
        // shows a progress bar forever. A workspace small enough to index inside the settling window
        // arrives here before the pipe is ready, so the send is deferred onto the window rather than
        // waited for — this runs on the indexing path and must not block it.
        if ( !_settled.IsCompleted )
        {
            _ = _settled.ContinueWith(
                _ => SendCompleted(filesIndexed, totalFiles, elapsed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return;
        }

        SendCompleted(filesIndexed, totalFiles, elapsed);
    }

    private void SendCompleted(int filesIndexed, int totalFiles, TimeSpan elapsed)
    {
        FlushStartedIfReady();

        _server.SendNotification(
            "gscode/indexingComplete",
            new IndexingCompleteParams(
                filesIndexed,
                totalFiles,
                (long)elapsed.TotalMilliseconds,
                Environment.WorkingSet / (1024.0 * 1024.0)));

        RequestCodeLensRefresh();
    }

    /// <summary>
    /// Asks the client to re-request every code lens.
    ///
    /// Reference COUNTS are a whole-workspace fact, so a lens rendered before indexing finished
    /// shows a number that was true only of the files seen so far — usually "0 references" on a
    /// function that has plenty. Nothing else invalidates them: the client re-requests on edit,
    /// which never covers a file the user is not looking at.
    ///
    /// Fire-and-forget: a client that does not support it just errors, and a failed refresh is
    /// cosmetic — the next edit re-requests anyway.
    /// </summary>
    private void RequestCodeLensRefresh()
    {
        // A REQUEST per the spec, not a notification: the client answers with null. Not awaited,
        // because Completed is called on the indexing path and must not block on the client.
        _ = _server.SendRequest("workspace/codeLens/refresh")
            .ReturningVoid(CancellationToken.None)
            .ContinueWith(static _ => { }, TaskScheduler.Default);
    }
}
