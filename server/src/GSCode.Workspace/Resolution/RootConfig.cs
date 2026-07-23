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
    /// is false, NO raw/mods roots are set regardless of overrides or TA_TOOLS_PATH —
    /// explicit off wins. Roots that do not exist on disk are dropped to null.
    /// </summary>
    public static RootConfig Create(
        bool rawEnabled,
        string? rawPathOverride,
        string? modsPathOverride,
        string? taToolsPath,
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

        string? rawRoot = ResolveRoot(rawPathOverride, taToolsPath, GameProfile.Active.RawSubfolder, fileSystem);
        string? modsRoot = ResolveRoot(modsPathOverride, taToolsPath, GameProfile.Active.ModsSubfolder, fileSystem);

        return new RootConfig
        {
            RawRoot = rawRoot,
            ModsRoot = modsRoot,
            WorkspaceFolders = folders.ToImmutable(),
        };
    }

    private static string? ResolveRoot(string? overridePath, string? taToolsPath, string? taToolsSubfolder, IFileSystem fileSystem)
    {
        string? candidate = null;

        if ( !string.IsNullOrWhiteSpace(overridePath) )
        {
            candidate = overridePath;
        }
        else if ( !string.IsNullOrWhiteSpace(taToolsPath) && !string.IsNullOrWhiteSpace(taToolsSubfolder) )
        {
            candidate = Path.Combine(taToolsPath, taToolsSubfolder);
        }

        if ( candidate is null )
        {
            return null;
        }

        string normalized = PathUtil.NormalizeAbsolute(candidate);
        if ( !fileSystem.DirectoryExists(normalized) )
        {
            return null;
        }

        return normalized;
    }
}
