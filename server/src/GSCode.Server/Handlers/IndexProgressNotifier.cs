using System.Diagnostics;
using GSCode.Workspace.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.Server.Handlers;

/// <summary>Payload for gscode/indexingStarted.</summary>
public sealed record IndexingStartedParams(int TotalFiles);

/// <summary>Payload for gscode/indexingProgress.</summary>
public sealed record IndexingProgressParams(int FilesIndexed, int TotalFiles);

/// <summary>Payload for gscode/indexingComplete.</summary>
public sealed record IndexingCompleteParams(int FilesIndexed, int TotalFiles, long ElapsedMilliseconds);

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
        _server.SendNotification("gscode/indexingStarted", new IndexingStartedParams(totalFiles));
    }

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
        _server.SendNotification(
            "gscode/indexingComplete",
            new IndexingCompleteParams(filesIndexed, totalFiles, (long)elapsed.TotalMilliseconds));
    }
}
