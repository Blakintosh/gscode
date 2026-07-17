namespace GSCode.Core.Paths;

/// <summary>
/// THE path normalizer — nothing else in the codebase calls Path.GetFullPath or invents
/// its own casing/separator rules. Both forms are lowercase-canonical and interned, so
/// normalized paths compare by reference and never need ignore-case comparers.
/// </summary>
public static class PathUtil
{
    /// <summary>
    /// Canonical form for an absolute on-disk path: full path, no trailing separator,
    /// lowercase, interned. This is the ScriptDatabase key format.
    /// </summary>
    public static string NormalizeAbsolute(string path)
    {
        string full = Path.GetFullPath(path);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return NameTable.Shared.InternLower(full);
    }

    /// <summary>
    /// Canonical form for a game-relative script path (e.g. from #using/#insert):
    /// backslash separators, trimmed, lowercase, interned.
    /// </summary>
    public static string NormalizeScriptPath(string scriptPath)
    {
        string cleaned = scriptPath.Trim().Replace('/', '\\');
        return NameTable.Shared.InternLower(cleaned);
    }

    /// <summary>True when <paramref name="path"/> sits underneath <paramref name="directory"/> (both already normalized).</summary>
    public static bool IsUnder(string path, string directory)
    {
        if ( path.Length <= directory.Length )
        {
            return false;
        }

        return path.StartsWith(directory, StringComparison.Ordinal)
            && path[directory.Length] == Path.DirectorySeparatorChar;
    }
}
