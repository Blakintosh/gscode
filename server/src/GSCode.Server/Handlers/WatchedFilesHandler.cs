using GSCode.Workspace.Indexing;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Applies workspace file create/change/delete events to the database. A branch switch
/// can fire hundreds at once, so all events in one batch are applied before returning.
/// Editor buffers are the source of truth for open files, so this skips them.
/// </summary>
public sealed class WatchedFilesHandler : DidChangeWatchedFilesHandlerBase
{
    private readonly WatchedFileUpdater _updater;

    public WatchedFilesHandler(WatchedFileUpdater updater)
    {
        _updater = updater;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        // GlobPattern's implicit string conversion trips a nullable false-positive here.
#pragma warning disable CS8601
        OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher[] watchers =
        [
            new() { GlobPattern = "**/*.gsc" },
            new() { GlobPattern = "**/*.csc" },
            new() { GlobPattern = "**/*.gsh" },
        ];
#pragma warning restore CS8601

        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(watchers),
        };
    }

    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        foreach ( FileEvent change in request.Changes )
        {
            WatchedFileChange kind = change.Type switch
            {
                FileChangeType.Created => WatchedFileChange.Created,
                FileChangeType.Deleted => WatchedFileChange.Deleted,
                _ => WatchedFileChange.Changed,
            };

            try
            {
                _updater.Apply(change.Uri.GetFileSystemPath(), kind);
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to apply watched-file change for {Uri}", change.Uri);
            }
        }

        return Unit.Task;
    }
}
