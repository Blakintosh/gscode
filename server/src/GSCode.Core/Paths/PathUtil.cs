namespace GSCode.Core.Paths;

/// <summary>
/// THE path normalizer — nothing else in the codebase calls Path.GetFullPath or invents
/// its own casing/separator rules. Both forms are canonical and interned, so normalized
/// paths compare by reference and never need ignore-case comparers.
/// </summary>
public static class PathUtil
{
    /// <summary>
    /// Whether absolute paths are lowercased into their canonical form. True where the
    /// filesystem is case-insensitive, so <c>C:\Raw</c> and <c>c:\raw</c> key identically.
    /// On Linux case is identity: lowercasing a path that contains an uppercase letter
    /// produces one that does not exist, so there the canonical form preserves case —
    /// still unambiguous, because every key derives from a single real disk path.
    /// </summary>
    private static readonly bool LowercaseAbsolutePaths =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Canonical form for an absolute on-disk path: full path, no trailing separator,
    /// lowercase on case-insensitive filesystems (exact case on Linux), interned. This
    /// is the ScriptDatabase key format.
    /// </summary>
    public static string NormalizeAbsolute(string path)
    {
        string full = Path.GetFullPath(path);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return LowercaseAbsolutePaths ? NameTable.Shared.InternLower(full) : NameTable.Shared.Intern(full);
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

    /// <summary>
    /// A script path without its extension — the form <c>#using</c> and <c>#include</c> name a file
    /// in. Unlike the two normalizers above this leaves case and separators alone, because its
    /// output is read by people: it feeds diagnostic messages and the directives a quick fix writes
    /// into the source, not the comparison keys.
    /// </summary>
    public static string WithoutExtension(string scriptPath)
    {
        return Path.ChangeExtension(scriptPath, null) ?? scriptPath;
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
