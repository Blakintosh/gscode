namespace GSCode.Workspace.Resolution;

/// <summary>Which world a file lives in — this decides what it can see.</summary>
public enum ResolutionContextKind
{
    /// <summary>A file inside share\raw: sees raw only.</summary>
    Raw,

    /// <summary>A file inside mods\&lt;name&gt;: sees its own mod overlay, then raw. Never other mods.</summary>
    Mod,

    /// <summary>Any other file: sees the workspace folders, then raw.</summary>
    Workspace,
}

/// <summary>
/// A file's resolution context, derived purely from its own path. Opening the entire
/// tools root as the workspace needs no special-casing: every file classifies itself.
/// </summary>
public readonly record struct ResolutionContext(ResolutionContextKind Kind, string? ModName, string? BaseFolder)
{
    /// <summary>Context for a raw game file.</summary>
    public static ResolutionContext RawContext { get; } = new(ResolutionContextKind.Raw, null, null);

    /// <summary>Context for a file in the named mod.</summary>
    public static ResolutionContext ForMod(string modName)
    {
        return new ResolutionContext(ResolutionContextKind.Mod, modName, null);
    }

    /// <summary>Context for a workspace file, anchored at its containing folder.</summary>
    public static ResolutionContext ForWorkspace(string baseFolder)
    {
        return new ResolutionContext(ResolutionContextKind.Workspace, null, baseFolder);
    }
}
