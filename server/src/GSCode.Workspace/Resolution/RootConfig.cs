using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;

namespace GSCode.Workspace.Resolution;

/// <summary>
/// The resolved root folders resolution works against. Built once at startup (and on
/// workspace-folder or setting changes). Null RawRoot/ModsRoot means workspace-only
/// mode — a first-class configuration, not an error state.
/// </summary>
public sealed record RootConfig
{
    /// <summary>The game raw folder (share\raw), or null when raw reading is disabled/unavailable.</summary>
    public string? RawRoot { get; init; }

    /// <summary>The mods folder, or null when unavailable. Each direct child is one isolated mod.</summary>
    public string? ModsRoot { get; init; }

    /// <summary>Open workspace folders, normalized.</summary>
    public ImmutableArray<string> WorkspaceFolders { get; init; } = [];

    /// <summary>A config with no roots at all (workspace-only, no folders).</summary>
    public static RootConfig Empty { get; } = new();

    /// <summary>
    /// The roots this workspace resolves against. Three inputs answering different questions: the
    /// WORKSPACE FOLDERS are where you are editing — usually a mod, which may live anywhere — while
    /// <paramref name="rawPath"/> and <paramref name="modsPath"/> say where the GAME is.
    ///
    /// A configured path always wins, and one naming a folder that is not on disk is dropped rather
    /// than trusted: a root under which every lookup misses would report the user's scripts as
    /// broken instead of the setting. Whatever is left unconfigured is then DERIVED, by walking up
    /// from the workspace folders looking for an install — see <see cref="FindRootAbove"/>. That
    /// covers the ordinary case, a mod at <c>&lt;install&gt;\mods\my_mod</c> or the install itself
    /// being open, without any configuration at all; a mod checked out elsewhere finds nothing and
    /// falls back to workspace-only, which is a first-class mode rather than an error.
    ///
    /// When <paramref name="rawEnabled"/> is false neither root is set by any route — explicit off
    /// beats both configuration and derivation.
    /// </summary>
    public static RootConfig Create(
        bool rawEnabled,
        string? rawPath,
        string? modsPath,
        IEnumerable<string> workspaceFolders,
        IFileSystem fileSystem,
        GameProfile? profile = null)
    {
        ImmutableArray<string>.Builder folders = ImmutableArray.CreateBuilder<string>();
        foreach ( string folder in workspaceFolders )
        {
            folders.Add(PathUtil.NormalizeAbsolute(folder));
        }

        ImmutableArray<string> normalizedFolders = folders.ToImmutable();

        if ( !rawEnabled )
        {
            return new RootConfig { WorkspaceFolders = normalizedFolders };
        }

        GameProfile layout = profile ?? GameProfile.Active;
        string? rawRoot = ResolveRoot(rawPath, fileSystem);
        string? modsRoot = ResolveRoot(modsPath, fileSystem);

        rawRoot ??= FindRootAbove(normalizedFolders, layout.RawSubfolder, fileSystem);

        // Mods is looked for above the raw root too, so that configuring rawPath alone still finds
        // the mods beside it. Without that, setting one path would silently cost you mod shadowing.
        if ( modsRoot is null )
        {
            ImmutableArray<string> searchFrom = rawRoot is null
                ? normalizedFolders
                : [rawRoot, .. normalizedFolders];

            modsRoot = FindRootAbove(searchFrom, layout.ModsSubfolder, fileSystem);
        }

        return new RootConfig
        {
            RawRoot = rawRoot,
            ModsRoot = modsRoot,
            WorkspaceFolders = normalizedFolders,
        };
    }

    /// <summary>The configured root, or null when it is unset or does not exist on disk.</summary>
    private static string? ResolveRoot(string? configured, IFileSystem fileSystem)
    {
        if ( string.IsNullOrWhiteSpace(configured) )
        {
            return null;
        }

        string normalized = PathUtil.NormalizeAbsolute(configured);
        if ( !fileSystem.DirectoryExists(normalized) )
        {
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// The first <c>&lt;ancestor&gt;\<paramref name="subfolder"/></c> that exists, searching each
    /// start folder and then its ancestors up to the drive. Returns the SUBFOLDER, not the install.
    ///
    /// Each start folder is exhausted before the next is tried, so an earlier workspace folder wins
    /// outright rather than losing to a shallower match under a later one — with several folders
    /// open, the order VSCode reports them in is the order the user chose.
    /// </summary>
    private static string? FindRootAbove(
        ImmutableArray<string> startFolders, string subfolder, IFileSystem fileSystem)
    {
        // Profiles spell subfolders in the game's own form ("share\raw"); on Linux that
        // backslash would otherwise be a literal character in a single directory name.
        string nativeSubfolder = subfolder.Replace('\\', Path.DirectorySeparatorChar);

        foreach ( string startFolder in startFolders )
        {
            string? candidate = startFolder;
            while ( candidate is not null )
            {
                string probe = PathUtil.NormalizeAbsolute(Path.Combine(candidate, nativeSubfolder));
                if ( fileSystem.DirectoryExists(probe) )
                {
                    return probe;
                }

                candidate = Path.GetDirectoryName(candidate);
            }
        }

        return null;
    }
}
