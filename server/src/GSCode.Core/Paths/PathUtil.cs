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
    private static readonly bool s_lowercaseAbsolutePaths =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Canonical form for an absolute on-disk path: full path, no trailing separator,
    /// lowercase on case-insensitive filesystems (exact case on Linux), interned. This
    /// is the ScriptDatabase key format.
    ///
    /// A root path — <c>C:\</c> on Windows, <c>/</c> everywhere else — is NEVER trimmed, even
    /// though it is also its own trailing separator: trimming it produced <c>C:</c>, a
    /// drive-RELATIVE path rather than an absolute one (<c>Path.GetFullPath("c:")</c> resolves
    /// against the current directory, not the drive root), and on Linux the same trim emptied the
    /// path to <c>""</c> entirely.
    /// </summary>
    public static string NormalizeAbsolute(string path)
    {
        string full = Path.GetFullPath(path);

        if ( !string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal) )
        {
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return s_lowercaseAbsolutePaths ? NameTable.Shared.InternLower(full) : NameTable.Shared.Intern(full);
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

    /// <summary>
    /// True when <paramref name="path"/> sits underneath <paramref name="directory"/> (both
    /// already normalized).
    ///
    /// <paramref name="directory"/> ending in a separator gets its own branch: that is what a
    /// normalized DRIVE ROOT looks like (<c>c:\</c>, or <c>/</c> on Linux — see
    /// <see cref="NormalizeAbsolute"/>), and the boundary check below assumes the opposite, that
    /// the directory's own separator is still ahead of it in <paramref name="path"/>. Indexing
    /// <c>path[directory.Length]</c> against a root directory landed one character INTO the first
    /// path segment instead of on the separator, so every path under a root drive compared
    /// falsely not-under it.
    /// </summary>
    public static bool IsUnder(string path, string directory)
    {
        if ( directory.Length > 0 && directory[^1] is '\\' or '/' )
        {
            return path.Length > directory.Length && path.StartsWith(directory, StringComparison.Ordinal);
        }

        if ( path.Length <= directory.Length )
        {
            return false;
        }

        return path.StartsWith(directory, StringComparison.Ordinal)
            && path[directory.Length] == Path.DirectorySeparatorChar;
    }
}
