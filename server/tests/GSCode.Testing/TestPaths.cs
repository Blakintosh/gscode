using GSCode.Workspace.Resolution;

namespace GSCode.Testing;

/// <summary>
/// The one fake location every in-memory workspace lives at. A test names a script by its
/// game-relative path (<c>scripts\lib.gsc</c>) and never spells a drive, so no two suites invent
/// different roots for the same idea.
///
/// Lower case because <see cref="GSCode.Core.Paths.PathUtil.NormalizeAbsolute"/> lowers every path
/// on Windows: a literal written this way compares equal to what the database stores.
/// </summary>
public static class TestPaths
{
    /// <summary>The raw root: the folder <c>#using scripts\lib</c> resolves under.</summary>
    public const string RawRoot = @"c:\raw";

    /// <summary>
    /// The mods root, for the many setups that configure one without putting anything in it. A test
    /// about mod overlays names its own mod folders under it.
    /// </summary>
    public const string ModsRoot = @"c:\mods";

    /// <summary>The absolute path of <paramref name="relativePath"/> under <see cref="RawRoot"/>.</summary>
    public static string Raw(string relativePath)
    {
        return Path.Combine(RawRoot, relativePath);
    }

    /// <summary>
    /// The standard roots over <paramref name="files"/>: raw enabled at <see cref="RawRoot"/>, mods at
    /// <see cref="ModsRoot"/>, no workspace folders. A test about roots builds its own config instead.
    /// </summary>
    public static RootConfig Config(IFileSystem files)
    {
        return RootConfig.Create(true, RawRoot, ModsRoot, [], files);
    }
}
