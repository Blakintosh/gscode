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

    public IndexProgressNotifier(ILanguageServerFacade server)
    {
        _server = server;
    }

    public void Started(int totalFiles)
    {
        // The server's own channel, not the client's. This line used to be written by the
        // extension host, which put the one message telling you indexing had begun in a different
        // output channel from every other thing the language server says — including whatever you
        // opened the channel to diagnose.
        Log.Information("Indexing {Count} script file(s)…", totalFiles);

        _server.SendNotification("gscode/indexingStarted", new IndexingStartedParams(totalFiles));
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
