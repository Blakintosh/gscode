using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using GSCode.NET.LSP;

namespace GSCode.NET.LSP.Handlers;

internal sealed class WorkspaceDidChangeWatchedFilesHandler : DidChangeWatchedFilesHandlerBase
{
    private readonly ILogger<WorkspaceDidChangeWatchedFilesHandler> _logger;
    private readonly ScriptManager _scriptManager;

    public WorkspaceDidChangeWatchedFilesHandler(
        ILogger<WorkspaceDidChangeWatchedFilesHandler> logger,
        ScriptManager scriptManager)
    {
        _logger = logger;
        _scriptManager = scriptManager;
    }

    public override async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("workspace/didChangeWatchedFiles received with {Count} change(s).", request.Changes.Count());

        // Forward changes to script manager to re-index and refresh dependents
        await _scriptManager.HandleWatchedFilesChangedAsync(request.Changes, cancellationToken);
        return Unit.Value;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        // Watch GSC/CSC scripts and GSH headers (inserted files)
        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.gsc"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.csc"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                },
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
                {
                    GlobPattern = new GlobPattern("**/*.gsh"),
                    Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
                }
            )
        };
    }
}