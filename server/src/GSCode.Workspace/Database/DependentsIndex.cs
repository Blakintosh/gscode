using System.Collections.Immutable;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files NAME a script path — through an <c>#include</c> or <c>#using</c> edge, or an inline
/// path call (<c>maps\mp\_utility::foo()</c>). The reverse of the dependency graph, in the same
/// normalized form as <see cref="RelativePathIndex"/>.
///
/// It exists for the reference query. When a function key resolves to one declaring file, the
/// references worth keeping are the ones that can reach that file — its own, and those of files
/// that import or path-call it (<see cref="DatabaseQueries.ScopeToIncludeGraph"/>) — and these are
/// the files this names. Collecting every reference to the key first costs the workspace: on a
/// merge dialect <c>main</c>'s key is every <c>main</c>, and at 50,000 files one CodeLens request
/// took 700 ms that way (PERF.md, the scale section).
/// </summary>
public sealed class DependentsIndex
{
    private readonly PackedInvertedIndex<string> _index = new(StringComparer.Ordinal);

    /// <summary>
    /// The paths one record names, normalized as <see cref="RelativePathIndex.Normalize"/> does —
    /// the same form the scoping rule compares them in. Insert edges are left out, as the rule
    /// leaves them out: an inserted header contributes text, not a reachable file.
    /// </summary>
    public static HashSet<string> KeysOf(ScriptRecord? record)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        if ( record is null )
        {
            return keys;
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert )
            {
                keys.Add(RelativePathIndex.Normalize(edge.RawPath));
            }
        }

        foreach ( GSCode.Parser.Extraction.PathCallReference pathCall in record.PathCallTargets )
        {
            keys.Add(RelativePathIndex.Normalize(pathCall.Path));
        }

        return keys;
    }

    /// <summary>Replaces one file's contribution.</summary>
    public void Apply(string path, HashSet<string> oldKeys, HashSet<string> newKeys)
    {
        _index.Apply(path, oldKeys, newKeys);
    }

    /// <summary>Paths of the files naming this normalized script path (snapshot).</summary>
    public ImmutableArray<string> FilesNaming(string normalizedScriptPath)
    {
        return _index.FilesFor(normalizedScriptPath);
    }
}
