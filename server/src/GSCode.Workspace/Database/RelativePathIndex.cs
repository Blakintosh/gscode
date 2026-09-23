using System.Collections.Immutable;
using GSCode.Core.Paths;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files sit at a script-relative path — the form a <c>#using</c> or <c>#include</c> names
/// one in: lowercase, backslashed, no extension (<c>scripts\shared\util_shared</c>). Usually one
/// file; more when a mod or workspace copy overlays a raw one, which is why the answer is a list
/// and every caller still applies visibility and shadowing to it.
///
/// It replaces a walk of the whole store. Statement-scope completion asked "which of my imports
/// declare which namespaces" by normalizing EVERY record's relative path and comparing it against
/// the import list, on every request; the merge dialect's include scope and the inline
/// <c>path::</c> completion did the same. At 1,000 files that is noise. At 50,000 it made a
/// completion's p99 four times its budget and growing with the workspace (PERF.md, the scale
/// section), while the files actually wanted were the handful the imports name.
///
/// PERF.md designed this index when completion was first measured and deferred it until a caller
/// needed it; the scale sweep is that caller.
/// </summary>
public sealed class RelativePathIndex
{
    private readonly PackedInvertedIndex<string> _index = new(StringComparer.Ordinal);

    /// <summary>
    /// The key one record contributes, or null when it sits outside every root and has no
    /// relative path. The same normalization the queries compare imports with, so a lookup here
    /// matches exactly the records the old comparison matched.
    /// </summary>
    public static string? KeyOf(ScriptRecord? record)
    {
        if ( record is null || record.RelativePath.Length == 0 )
        {
            return null;
        }

        return Normalize(record.RelativePath);
    }

    /// <summary>
    /// A script path in the index's key form — and THE comparison key for a script path written in
    /// a directive: canonical script form, minus the extension, because <c>#using</c> and
    /// <c>#include</c> name a file without one.
    ///
    /// Every caller that compares a written path against this index must fold it here rather than
    /// spell the two calls itself. The reachability queries in
    /// <see cref="DatabaseQueries.FindReferencesReaching"/> look their answer up in
    /// <see cref="LanguageStore.FilesAt"/> and <see cref="LanguageStore.FilesNaming"/>, which are
    /// keyed on this — a second spelling that drifted would match nothing and report an empty
    /// result rather than an error.
    /// </summary>
    public static string Normalize(string scriptPath)
    {
        return PathUtil.WithoutExtension(PathUtil.NormalizeScriptPath(scriptPath));
    }

    /// <summary>Moves one file from its old key to its new one.</summary>
    public void Apply(string path, string? oldKey, string? newKey)
    {
        if ( string.Equals(oldKey, newKey, StringComparison.Ordinal) )
        {
            return;
        }

        HashSet<string> oldKeys = new(StringComparer.Ordinal);
        if ( oldKey is not null )
        {
            oldKeys.Add(oldKey);
        }

        HashSet<string> newKeys = new(StringComparer.Ordinal);
        if ( newKey is not null )
        {
            newKeys.Add(newKey);
        }

        _index.Apply(path, oldKeys, newKeys);
    }

    /// <summary>Paths of the files at this normalized script path (snapshot).</summary>
    public ImmutableArray<string> FilesAt(string normalizedScriptPath)
    {
        return _index.FilesFor(normalizedScriptPath);
    }
}
