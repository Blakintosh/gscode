using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Workspace.Database;
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
/// a full pass — unchanged files restore from the in-memory cache snapshot, so the cost is a
/// warm start rather than a cold one.
/// </summary>
public sealed class WorkspaceFoldersHandler : DidChangeWorkspaceFoldersHandlerBase
{
    private readonly ResolverHolder _resolver;
    private readonly ServerSettings _settings;
    private readonly IFileSystem _fileSystem;
    private readonly ScriptDatabase _database;
    private readonly WorkspaceIndexer _indexer;

    public WorkspaceFoldersHandler(
        ResolverHolder resolver,
        ServerSettings settings,
        IFileSystem fileSystem,
        ScriptDatabase database,
        WorkspaceIndexer indexer)
    {
        _resolver = resolver;
        _settings = settings;
        _fileSystem = fileSystem;
        _database = database;
        _indexer = indexer;
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
            IndexOutcome outcome = await _indexer
                .IndexAsync(IndexingModeFor(_settings), NullIndexProgressListener.Instance, cancellationToken)
                .ConfigureAwait(false);

            Log.Information(
                "Re-indexed after folder change: {Total} files ({Restored} from cache)",
                outcome.Total,
                outcome.Restored);
        }

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

    private static IndexingMode IndexingModeFor(ServerSettings settings)
    {
        if ( string.Equals(settings.WorkspaceIndexingMode, "off", StringComparison.OrdinalIgnoreCase) )
        {
            return IndexingMode.Off;
        }

        if ( string.Equals(settings.WorkspaceIndexingMode, "full", StringComparison.OrdinalIgnoreCase) )
        {
            return IndexingMode.Full;
        }

        return IndexingMode.Partial;
    }
}
