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
    /// Builds the config from settings + environment. When <paramref name="rawEnabled"/>
    /// is false, NO raw/mods roots are set regardless of the configured paths —
    /// explicit off wins. Roots that do not exist on disk are dropped to null.
    /// </summary>
    /// <summary>
    /// The roots this workspace resolves against. Three inputs, and they answer different questions:
    /// the WORKSPACE FOLDERS are where you are editing — usually a mod, which may live anywhere —
    /// while <paramref name="rawPath"/> and <paramref name="modsPath"/> say where the GAME is. Those
    /// are settings rather than anything discovered, because only one game in the lineage ships a
    /// tools environment variable to discover it from, and a mod folder tells you nothing about
    /// which install it belongs to.
    /// </summary>
    public static RootConfig Create(
        bool rawEnabled,
        string? rawPath,
        string? modsPath,
        IEnumerable<string> workspaceFolders,
        IFileSystem fileSystem)
    {
        ImmutableArray<string>.Builder folders = ImmutableArray.CreateBuilder<string>();
        foreach ( string folder in workspaceFolders )
        {
            folders.Add(PathUtil.NormalizeAbsolute(folder));
        }

        if ( !rawEnabled )
        {
            return new RootConfig { WorkspaceFolders = folders.ToImmutable() };
        }

        string? rawRoot = ResolveRoot(rawPath, fileSystem);
        string? modsRoot = ResolveRoot(modsPath, fileSystem);

        return new RootConfig
        {
            RawRoot = rawRoot,
            ModsRoot = modsRoot,
            WorkspaceFolders = folders.ToImmutable(),
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
}
