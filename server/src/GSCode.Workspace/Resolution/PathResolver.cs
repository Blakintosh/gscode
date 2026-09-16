using System.Collections.Concurrent;
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

    /// <summary>
    /// Memoizes <see cref="Resolve"/> by (context, relative path) — including a MISS, since a
    /// miss is the expensive case: it walks every configured root before returning null, and a
    /// broken or not-yet-created import is asked about on every keystroke by two independent
    /// callers (<see cref="Analysis.FileImports"/> and <see cref="Analysis.UsingNotFoundLint"/>
    /// each resolve the same directive list), so an uncached miss is paid twice per analysis and
    /// again on every later one. A confirmed 4x-by-root-count, 2x-by-caller multiplier on an
    /// adversarial workspace — no wall-clock claim, since that depends on the filesystem, but the
    /// probe count is real and unbounded by nothing else. Invalidated wholesale by
    /// <see cref="InvalidateResolutionCache"/> on any watched create/delete, which is coarse but
    /// correct and cheap next to a probe: those events are user-paced, never per-keystroke.
    /// </summary>
    private readonly ConcurrentDictionary<(ResolutionContext Context, string Relative), string?> _resolveCache = new();

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
    ///
    /// Memoized, including the null answer — see <see cref="_resolveCache"/> for why a miss is
    /// the case that matters most here.
    /// </summary>
    public string? Resolve(ResolutionContext context, string scriptPathWithExtension)
    {
        string relative = PathUtil.NormalizeScriptPath(scriptPathWithExtension);

        if ( relative.Length == 0 || IsIllegalScriptPath(relative) )
        {
            return null;
        }

        (ResolutionContext, string) key = (context, relative);
        if ( _resolveCache.TryGetValue(key, out string? cached) )
        {
            return cached;
        }

        string? resolved = ResolveUncached(context, relative);

        // A last-write-wins race between two threads resolving the same key concurrently is fine:
        // both computed the same answer from the same (unmoving, for the duration of one probe)
        // filesystem state, so whichever write lands is correct either way.
        _resolveCache[key] = resolved;
        return resolved;
    }

    private string? ResolveUncached(ResolutionContext context, string relative)
    {
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
    /// Forgets every memoized resolution. Called on any watched file create or delete: a create
    /// can turn a cached miss into a hit, and a delete can turn a cached hit into a miss, and
    /// nothing here tracks which specific keys a given path could affect — a file might be named
    /// by any relative path from any context. Coarse, but cheap next to what it protects against:
    /// these events are user-paced (a save, a branch switch), never per-keystroke, so clearing the
    /// whole cache costs a handful of re-probes on the next few analyses rather than one that
    /// never resolves a rename or a newly created import.
    /// </summary>
    public void InvalidateResolutionCache()
    {
        _resolveCache.Clear();
    }

    /// <summary>
    /// The script-relative identity of a file under its context's root (the overlay
    /// shadowing key), or "" when it sits outside every root.
    ///
    /// NORMALIZES ITS ARGUMENT, and the parameter name is kept as a statement of what the roots are
    /// compared against rather than as a demand on the caller. It used to be a demand, and the
    /// failure was silent in the worst way: an unnormalized path fails <see cref="PathUtil.IsUnder"/>
    /// against a normalized root, "" comes back, that empty string becomes
    /// <c>ScriptRecord.RelativePath</c>, and every import match downstream compares against it and
    /// never fires. Nothing throws — the workspace simply behaves as though no file included
    /// anything. <see cref="GetContext"/> has always normalized on entry; this is the same contract,
    /// and the cost is one idempotent call on a path already in that form.
    /// </summary>
    public string GetScriptRelativePath(string absolutePath, ResolutionContext context)
    {
        string normalizedAbsolutePath = PathUtil.NormalizeAbsolute(absolutePath);

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
        ImmutableArray<string> extensions = GameProfile.Active.ScriptExtensions;

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

        foreach ( string rootFolder in OutermostRoots(rootFolders) )
        {
            // One walk per root for ALL extensions, rather than one per extension: the tree is
            // large (160,382 files in a Black Ops 1 install) and walking it once per script
            // extension re-read every directory three times to find the same 2,960 files.
            foreach ( string file in _fileSystem.EnumerateFilesWithExtensions(rootFolder, extensions) )
            {
                string normalized = PathUtil.NormalizeAbsolute(file);
                if ( seen.Add(normalized) )
                {
                    yield return normalized;
                }
            }
        }
    }

    /// <summary>
    /// The given roots with any that sit inside another dropped.
    ///
    /// Nothing stopped them overlapping, and they routinely do: opening the tools install as a
    /// workspace folder while rawPath points inside it walked the raw tree twice, and the caller's
    /// <c>seen</c> set only deduplicated the RESULTS — the second walk still happened in full. A
    /// contained root can contribute no file its container does not, so dropping it is free.
    /// </summary>
    private static List<string> OutermostRoots(List<string> rootFolders)
    {
        List<string> normalized = [];
        foreach ( string folder in rootFolders )
        {
            normalized.Add(PathUtil.NormalizeAbsolute(folder));
        }

        List<string> outermost = [];
        for ( int index = 0; index < rootFolders.Count; index++ )
        {
            bool covered = false;
            for ( int other = 0; other < normalized.Count && !covered; other++ )
            {
                if ( other == index )
                {
                    continue;
                }

                // An exact duplicate is covered by whichever copy comes first, so that one survives
                // and later copies do not — IsUnder is false for equal paths, hence the tie-break.
                bool duplicate = string.Equals(normalized[index], normalized[other], StringComparison.Ordinal);
                covered = PathUtil.IsUnder(normalized[index], normalized[other])
                    || (duplicate && other < index);
            }

            if ( !covered )
            {
                outermost.Add(rootFolders[index]);
            }
        }

        return outermost;
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
