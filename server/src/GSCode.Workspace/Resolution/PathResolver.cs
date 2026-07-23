using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;

namespace GSCode.Workspace.Resolution;

/// <summary>
/// The single authority for two questions: which context does a file live in, and which
/// absolute file does a game-relative script path resolve to from that context.
/// Probe order: Mod(m) → [mods\m, raw] · Raw → [raw] · Workspace → [base, other folders, raw].
/// </summary>
public sealed class PathResolver
{
    private readonly RootConfig _config;
    private readonly IFileSystem _fileSystem;

    public PathResolver(RootConfig config, IFileSystem fileSystem)
    {
        _config = config;
        _fileSystem = fileSystem;
    }

    /// <summary>The configuration this resolver was built from.</summary>
    public RootConfig Config
    {
        get { return _config; }
    }

    /// <summary>
    /// Classifies a file by its own path prefix: mods\&lt;name&gt; → Mod, share\raw → Raw,
    /// else Workspace (anchored at its workspace folder, or its own directory when the
    /// file is outside every configured folder).
    /// </summary>
    public ResolutionContext GetContext(string absolutePath)
    {
        string normalized = PathUtil.NormalizeAbsolute(absolutePath);

        if ( _config.ModsRoot is not null && PathUtil.IsUnder(normalized, _config.ModsRoot) )
        {
            string remainder = normalized[(_config.ModsRoot.Length + 1)..];
            int separatorIndex = remainder.IndexOf(Path.DirectorySeparatorChar);
            string modName = separatorIndex < 0 ? remainder : remainder[..separatorIndex];
            return ResolutionContext.ForMod(modName);
        }

        if ( _config.RawRoot is not null && PathUtil.IsUnder(normalized, _config.RawRoot) )
        {
            return ResolutionContext.RawContext;
        }

        foreach ( string folder in _config.WorkspaceFolders )
        {
            if ( PathUtil.IsUnder(normalized, folder) )
            {
                return ResolutionContext.ForWorkspace(folder);
            }
        }

        string containingDirectory = Path.GetDirectoryName(normalized) ?? normalized;
        return ResolutionContext.ForWorkspace(containingDirectory);
    }

    /// <summary>
    /// Resolves a game-relative script path (e.g. "scripts\shared\util_shared.gsc") from
    /// the given context. Returns the normalized absolute path of the first existing
    /// candidate, or null. Rooted paths and ".." traversal are rejected outright.
    /// </summary>
    public string? Resolve(ResolutionContext context, string scriptPathWithExtension)
    {
        string relative = PathUtil.NormalizeScriptPath(scriptPathWithExtension);

        if ( relative.Length == 0 || IsIllegalScriptPath(relative) )
        {
            return null;
        }

        foreach ( string root in RootsFor(context) )
        {
            string candidate = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
            string normalizedCandidate = PathUtil.NormalizeAbsolute(candidate);

            if ( _fileSystem.FileExists(normalizedCandidate) )
            {
                return normalizedCandidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The script-relative identity of a file under its context's root (the overlay
    /// shadowing key), or "" when it sits outside every root.
    /// </summary>
    public string GetScriptRelativePath(string normalizedAbsolutePath, ResolutionContext context)
    {
        switch ( context.Kind )
        {
            case ResolutionContextKind.Raw:
            {
                if ( _config.RawRoot is not null && PathUtil.IsUnder(normalizedAbsolutePath, _config.RawRoot) )
                {
                    return normalizedAbsolutePath[(_config.RawRoot.Length + 1)..];
                }

                return "";
            }
            case ResolutionContextKind.Mod:
            {
                if ( _config.ModsRoot is not null && context.ModName is not null )
                {
                    string modRoot = Path.Combine(_config.ModsRoot, context.ModName);
                    if ( PathUtil.IsUnder(normalizedAbsolutePath, modRoot) )
                    {
                        return normalizedAbsolutePath[(modRoot.Length + 1)..];
                    }
                }

                return "";
            }
            default:
            {
                if ( context.BaseFolder is not null && PathUtil.IsUnder(normalizedAbsolutePath, context.BaseFolder) )
                {
                    return normalizedAbsolutePath[(context.BaseFolder.Length + 1)..];
                }

                return "";
            }
        }
    }

    /// <summary>
    /// Every script and header file the cold-start indexer should visit: raw, each mod,
    /// and all workspace folders (deduplicated by normalized path).
    /// </summary>
    public IEnumerable<string> EnumerateIndexTargets()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        ImmutableArray<string> patterns = GameProfile.Active.ScriptGlobs;

        List<string> rootFolders = [];
        if ( _config.RawRoot is not null )
        {
            rootFolders.Add(_config.RawRoot);
        }

        if ( _config.ModsRoot is not null )
        {
            rootFolders.Add(_config.ModsRoot);
        }

        foreach ( string folder in _config.WorkspaceFolders )
        {
            rootFolders.Add(folder);
        }

        foreach ( string rootFolder in rootFolders )
        {
            foreach ( string pattern in patterns )
            {
                foreach ( string file in _fileSystem.EnumerateFiles(rootFolder, pattern) )
                {
                    string normalized = PathUtil.NormalizeAbsolute(file);
                    if ( seen.Add(normalized) )
                    {
                        yield return normalized;
                    }
                }
            }
        }
    }

    private List<string> RootsFor(ResolutionContext context)
    {
        List<string> roots = [];

        if ( context.Kind == ResolutionContextKind.Mod && context.ModName is not null )
        {
            if ( _config.ModsRoot is not null )
            {
                roots.Add(Path.Combine(_config.ModsRoot, context.ModName));
            }

            if ( _config.RawRoot is not null )
            {
                roots.Add(_config.RawRoot);
            }

            return roots;
        }

        if ( context.Kind == ResolutionContextKind.Raw )
        {
            if ( _config.RawRoot is not null )
            {
                roots.Add(_config.RawRoot);
            }

            return roots;
        }

        // Workspace: the file's own folder first, then the other workspace folders, then raw.
        if ( context.BaseFolder is not null )
        {
            roots.Add(context.BaseFolder);
        }

        foreach ( string folder in _config.WorkspaceFolders )
        {
            if ( !string.Equals(folder, context.BaseFolder, StringComparison.Ordinal) )
            {
                roots.Add(folder);
            }
        }

        if ( _config.RawRoot is not null )
        {
            roots.Add(_config.RawRoot);
        }

        return roots;
    }

    private static bool IsIllegalScriptPath(string relative)
    {
        // Mirrors the engine's constraints: no rooted paths, no drive letters, no traversal.
        if ( relative[0] == '\\' )
        {
            return true;
        }

        if ( relative.Length >= 2 && relative[1] == ':' )
        {
            return true;
        }

        return relative.Contains("..", StringComparison.Ordinal);
    }
}
