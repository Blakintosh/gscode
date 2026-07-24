using System.Collections.Frozen;
using GSCode.Core;

namespace GSCode.Workspace.Api;

/// <summary>
/// The list of script files that shipped with the mod tools, keyed by raw-relative path.
/// Powers <c>gscode.rawFileWarningMode = "stock"</c>: editing a stock script is almost always
/// a mistake, while editing a user-authored file that happens to live under raw is not.
/// </summary>
public sealed class StockScripts
{
    private readonly FrozenSet<string> _paths;

    public static StockScripts Empty { get; } = new(FrozenSet<string>.Empty);

    private StockScripts(FrozenSet<string> paths)
    {
        _paths = paths;
    }

    public int Count => _paths.Count;

    /// <summary>
    /// Loads the bundled list named by the profile; empty when the profile ships none, or the file
    /// is missing/unreadable, rather than throwing.
    /// </summary>
    public static StockScripts Load(string apiDirectory, GameProfile? profile = null)
    {
        if ( (profile ?? GameProfile.Active).StockScriptsFileName is not string stockScriptsFile )
        {
            return Empty;
        }

        string path = Path.Combine(apiDirectory, stockScriptsFile);
        if ( !File.Exists(path) )
        {
            return Empty;
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach ( string line in File.ReadLines(path) )
        {
            string trimmed = line.Trim();
            if ( trimmed.Length == 0 || trimmed.StartsWith('#') )
            {
                continue;
            }

            paths.Add(Canonical(trimmed));
        }

        return new StockScripts(paths.ToFrozenSet(StringComparer.Ordinal));
    }

    /// <summary>True when the given raw-relative path names a stock script. Slash style and casing are irrelevant.</summary>
    public bool Contains(string relativePath)
    {
        if ( relativePath.Length == 0 )
        {
            return false;
        }

        return _paths.Contains(Canonical(relativePath));
    }

    /// <summary>Both slash styles and any casing collapse onto one key; the file itself uses forward slashes.</summary>
    private static string Canonical(string relativePath)
    {
        return relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }
}
