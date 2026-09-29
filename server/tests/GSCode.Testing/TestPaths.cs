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

    /// <summary>The absolute path of <paramref name="relativePath"/> under <see cref="RawRoot"/>.</summary>
    public static string Raw(string relativePath)
    {
        return Path.Combine(RawRoot, relativePath);
    }
}
