using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Server.Configuration;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Rebuilds resolution when the client adds or removes workspace folders, so a multi-root
/// workspace does not need a server restart to see a new folder.
///
/// Three things have to happen in order: the resolver is swapped first, since every later
/// query classifies paths through it; records under removed folders are dropped, because
/// their files are no longer visible; and the added folders are indexed last. Re-indexing is
/// a full pass — unchanged files restore from the cache snapshot, so the cost is a warm start
/// rather than a cold one.
///
/// This is the only place that indexes twice in one session, and the snapshot is released when a
/// pass finishes rather than kept for the run — a bo3 workspace's blobs are 21 MB and a bo1 one's
/// 64, against a 400 MB steady-state budget. So it is re-read here, which is the 13–54 ms this
/// path pays to keep the other case free.
/// </summary>
public sealed class WorkspaceFoldersHandler : DidChangeWorkspaceFoldersHandlerBase
{
    private readonly ResolverHolder _resolver;
    private readonly ServerSettings _settings;
    private readonly IFileSystem _fileSystem;
    private readonly ScriptDatabase _database;
    private readonly WorkspaceIndexer _indexer;
    private readonly DocumentStore _documents;
    private readonly WorkspaceLintSweep _lintSweep;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;
    private readonly DependentDiagnosticsRefresher _dependents;

    public WorkspaceFoldersHandler(
        ResolverHolder resolver,
        ServerSettings settings,
        IFileSystem fileSystem,
        ScriptDatabase database,
        WorkspaceIndexer indexer,
        DocumentStore documents,
        WorkspaceLintSweep lintSweep,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        DependentDiagnosticsRefresher dependents)
    {
        _lintSweep = lintSweep;
        _workspaceDiagnostics = workspaceDiagnostics;
        _dependents = dependents;
        _resolver = resolver;
        _settings = settings;
        _fileSystem = fileSystem;
        _database = database;
        _indexer = indexer;
        _documents = documents;
    }

    protected override DidChangeWorkspaceFolderRegistrationOptions CreateRegistrationOptions(ClientCapabilities clientCapabilities)
    {
        return new DidChangeWorkspaceFolderRegistrationOptions();
    }

    public override async Task<Unit> Handle(DidChangeWorkspaceFoldersParams request, CancellationToken cancellationToken)
    {
        ImmutableArray<string> updated = NextFolderSet(request);

        RootConfig rebuilt = BuildConfig(_settings, updated, _fileSystem);
        _resolver.Current = new PathResolver(rebuilt, _fileSystem);

        int dropped = DropRecordsOutsideFolders(request);

        Log.Information(
            "Workspace folders changed: {FolderCount} folder(s) in scope, {Dropped} record(s) dropped",
            rebuilt.WorkspaceFolders.Length,
            dropped);

        // Only worth re-indexing when a folder was added; a pure removal has nothing new.
        if ( request.Event.Added.Any() )
        {
            // reloadSnapshot, not a separate ReloadRestoreSnapshot() call before this: both happen
            // under the indexer's own pass gate, so a startup pass in flight cannot have its snapshot
            // swapped out from under it in the gap between two calls.
            IndexingMode mode = _settings.IndexingMode;
            IndexOutcome outcome = await _indexer
                .IndexAsync(
                    mode, NullIndexProgressListener.Instance, cancellationToken,
                    reloadSnapshot: true, ownedByEditor: _documents.IsOpen)
                .ConfigureAwait(false);

            Log.Information(
                "Re-indexed after folder change: {Total} files ({Restored} from cache)",
                outcome.Total,
                outcome.Restored);

            // The new folder's files were indexed with their parse diagnostics alone. Startup sweeps
            // the cross-file lints in full mode, and a folder added later has to be swept the same
            // way, or its files report less than the ones that were there at start. The whole set,
            // not the new folder's: an added file can resolve a call another file reported missing.
            if ( mode == IndexingMode.Full )
            {
                await _lintSweep.RunFullSweepAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Either way the set of files moved under every diagnostic in the Problems panel: a removed
        // folder's problems have to be taken back, an added one's published, and every open file
        // was linted against the old set. Startup does the same pair after its own index.
        _workspaceDiagnostics.Refresh();
        _dependents.Schedule();

        return Unit.Value;
    }

    private ImmutableArray<string> NextFolderSet(DidChangeWorkspaceFoldersParams request)
    {
        List<string> removed = [];
        foreach ( WorkspaceFolder folder in request.Event.Removed )
        {
            removed.Add(folder.Uri.GetFileSystemPath());
        }

        List<string> added = [];
        foreach ( WorkspaceFolder folder in request.Event.Added )
        {
            added.Add(folder.Uri.GetFileSystemPath());
        }

        return NextFolderSet(_resolver.Current.Config.WorkspaceFolders, removed, added);
    }

    /// <summary>
    /// The current folder set with removals taken out and additions put in, every entry
    /// normalized so a folder named differently by the client still matches what is stored.
    /// Removals are applied first, so a folder that is both removed and re-added survives.
    /// </summary>
    public static ImmutableArray<string> NextFolderSet(
        IEnumerable<string> current,
        IEnumerable<string> removed,
        IEnumerable<string> added)
    {
        HashSet<string> folders = new(StringComparer.Ordinal);
        foreach ( string folder in current )
        {
            folders.Add(PathUtil.NormalizeAbsolute(folder));
        }

        foreach ( string folder in removed )
        {
            folders.Remove(PathUtil.NormalizeAbsolute(folder));
        }

        foreach ( string folder in added )
        {
            folders.Add(PathUtil.NormalizeAbsolute(folder));
        }

        return [.. folders];
    }

    /// <summary>
    /// Whether a record should be forgotten when a folder leaves the workspace. Only
    /// workspace-context records qualify: raw and mod files stay reachable regardless of which
    /// folders happen to be open, so dropping them would break resolution for every other file.
    /// </summary>
    public static bool ShouldDropOnFolderRemoval(ScriptRecord record, string removedFolder)
    {
        return record.ContextId.StartsWith("workspace:", StringComparison.Ordinal)
            && PathUtil.IsUnder(record.Path, PathUtil.NormalizeAbsolute(removedFolder));
    }

    /// <summary>Forgets every record under a removed folder; its files are no longer visible.</summary>
    private int DropRecordsOutsideFolders(DidChangeWorkspaceFoldersParams request)
    {
        int dropped = 0;

        foreach ( WorkspaceFolder removed in request.Event.Removed )
        {
            string folder = PathUtil.NormalizeAbsolute(removed.Uri.GetFileSystemPath());
            dropped += DropUnder(_database.Gsc, folder, GSCode.Core.Symbols.ScriptLanguage.Gsc);
            dropped += DropUnder(_database.Csc, folder, GSCode.Core.Symbols.ScriptLanguage.Csc);
            dropped += DropGshUnder(folder);
        }

        return dropped;
    }

    private int DropUnder(LanguageStore store, string folder, GSCode.Core.Symbols.ScriptLanguage language)
    {
        List<string> paths = [];
        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( ShouldDropOnFolderRemoval(record, folder) )
            {
                paths.Add(record.Path);
            }
        }

        foreach ( string path in paths )
        {
            _database.Remove(path, language);
        }

        return paths.Count;
    }

    private int DropGshUnder(string folder)
    {
        List<string> paths = [];
        foreach ( ScriptRecord record in _database.AllGshRecords )
        {
            if ( ShouldDropOnFolderRemoval(record, folder) )
            {
                paths.Add(record.Path);
            }
        }

        foreach ( string path in paths )
        {
            _database.RemoveGsh(path);
        }

        return paths.Count;
    }

    /// <summary>Rebuilds the root configuration from settings plus the given folder set.</summary>
    public static RootConfig BuildConfig(ServerSettings settings, IEnumerable<string> workspaceFolders, IFileSystem fileSystem)
    {
        // Settings only. The game install is not discovered: one game in the lineage ships an
        // environment variable pointing at its tools, and a workspace folder — typically a mod that
        // lives nowhere near the install — cannot imply which install it belongs to. So the user
        // says where the game is, and that is the same answer for every game.
        return RootConfig.Create(
            settings.RawEnabled,
            settings.RawPath.Length == 0 ? null : settings.RawPath,
            settings.ModsPath.Length == 0 ? null : settings.ModsPath,
            workspaceFolders,
            fileSystem);
    }
}
