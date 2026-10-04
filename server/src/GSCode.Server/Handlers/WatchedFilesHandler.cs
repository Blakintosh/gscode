using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Server.Configuration;
using FileSystemWatcher = OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher;

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
    private readonly WorkspaceLintSweep _lintSweep;
    private readonly ServerSettings _settings;

    public WatchedFilesHandler(
        WatchedFileUpdater updater,
        ScriptDatabase database,
        DocumentStore documents,
        DependentDiagnosticsRefresher dependents,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        WorkspaceLintSweep lintSweep,
        ServerSettings settings)
    {
        _lintSweep = lintSweep;
        _settings = settings;
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
        FileSystemWatcher[] watchers =
        [
            .. GameProfile.Active.ScriptGlobs.Select(glob =>
                new FileSystemWatcher { GlobPattern = "**/" + glob }),
        ];
#pragma warning restore CS8601

        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<FileSystemWatcher>(watchers),
        };
    }

    public override async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        bool applied = false;

        // Every record the batch rewrote — a changed file, and every file a changed header is
        // inserted into — and the files whose exports moved, which are what other files'
        // diagnostics are computed against.
        HashSet<string> touched = new(StringComparer.Ordinal);
        List<string> exportsMoved = [];

        // The editor's buffer wins for any open file the update would rewrite — the changed file
        // itself, and every dependent of a changed header. The text-sync handler analyses an open
        // document on open, change and save, so re-reading disk here would either duplicate that
        // work or, with unsaved edits, quietly replace its record with older content.
        bool OwnedByEditor(string candidate)
        {
            return _documents.IsOpen(candidate);
        }

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
                ulong before = SignatureOf(path);
                touched.UnionWith(_updater.Apply(path, kind, OwnedByEditor));
                if ( SignatureOf(path) != before )
                {
                    exportsMoved.Add(PathUtil.NormalizeAbsolute(path));
                }

                applied = true;
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to apply watched-file change for {Uri}", change.Uri);
            }
        }

        // A re-index stores the parse diagnostics alone. In full mode a closed file reports its
        // cross-file problems too, so every record the update rewrote is linted again before it is
        // published — a branch switch otherwise emptied the Problems panel of every file it touched.
        if ( touched.Count > 0 && _settings.IndexingMode == IndexingMode.Full && _database.HasCompletedIndex )
        {
            try
            {
                await _lintSweep.RelintClosedFilesAsync(touched, cancellationToken).ConfigureAwait(false);
            }
            catch ( OperationCanceledException )
            {
                // The next change, or the next start, lints these again.
            }
        }

        // Closed files carry their own stored diagnostics, which the update above just recomputed;
        // nothing was republishing them, so a file fixed on disk kept showing its old problems.
        // Cheap: this republishes what is already stored rather than re-analysing anything.
        if ( applied )
        {
            _workspaceDiagnostics.Refresh();
        }

        // Open files are computed against the changed ones, so every one of them is a dependent.
        // Naming each changed file as an origin also re-lints, in full mode, the CLOSED files that
        // call its functions. A deleted file has no record left to read its functions from, so its
        // closed callers wait for the next change or start; the open ones are still refreshed.
        foreach ( string origin in exportsMoved )
        {
            _dependents.Schedule(origin);
        }

        return Unit.Value;
    }

    /// <summary>The file's export signature, or 0 when it is not (or no longer) indexed.</summary>
    private ulong SignatureOf(string path)
    {
        return _database.TryGetAnyRecord(path, out ScriptRecord record) ? ExportSignature.Of(record) : 0;
    }
}
