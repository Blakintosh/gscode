using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
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
    private readonly ScriptDatabase _database;
    private readonly DocumentStore _documents;
    private readonly DependentDiagnosticsRefresher _dependents;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;

    public WatchedFilesHandler(
        WatchedFileUpdater updater,
        ScriptDatabase database,
        DocumentStore documents,
        DependentDiagnosticsRefresher dependents,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics)
    {
        _updater = updater;
        _database = database;
        _documents = documents;
        _dependents = dependents;
        _workspaceDiagnostics = workspaceDiagnostics;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        // GlobPattern's implicit string conversion trips a nullable false-positive here.
#pragma warning disable CS8601
        OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher[] watchers =
        [
            .. GSCode.Core.GameProfile.Active.ScriptGlobs.Select(glob =>
                new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher { GlobPattern = "**/" + glob }),
        ];
#pragma warning restore CS8601

        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(watchers),
        };
    }

    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        bool exportsMoved = false;
        bool applied = false;

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
                string path = change.Uri.GetFileSystemPath();

                // Whether this change is one an OPEN file's diagnostics could notice. Read either
                // side of the update, the same test the edit path uses — a branch switch that
                // rewrites a hundred bodies moves no signature and needs no re-linting.
                // The editor's buffer wins for an open file: the text-sync handler analyses it on
                // open, change and save, so re-reading disk here would either duplicate that work
                // or, with unsaved edits, quietly replace the buffer's record with older content.
                bool ownedByEditor = _documents.TryGet(path, out OpenDocument _);

                ulong before = SignatureOf(path);
                _updater.Apply(path, kind, ownedByEditor);
                exportsMoved |= SignatureOf(path) != before;
                applied = true;
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to apply watched-file change for {Uri}", change.Uri);
            }
        }

        // Closed files carry their own stored diagnostics, which the update above just recomputed;
        // nothing was republishing them, so a file fixed on disk kept showing its old problems.
        // Cheap: this republishes what is already stored rather than re-analysing anything.
        if ( applied )
        {
            _workspaceDiagnostics.Refresh();
        }

        // Open files are computed against the changed ones, and a change arriving behind the
        // editor's back belongs to no open document — so every one of them is a dependent.
        if ( exportsMoved )
        {
            _dependents.Schedule();
        }

        return Unit.Task;
    }

    /// <summary>The file's export signature, or 0 when it is not (or no longer) indexed.</summary>
    private ulong SignatureOf(string path)
    {
        return _database.TryGetAnyRecord(path, out ScriptRecord record) ? ExportSignature.Of(record) : 0;
    }
}
