using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// Namespace → the files declaring a function in it, so a question about one namespace does not read
/// the whole store.
///
/// `FunctionsInNamespace` walked every record and every function in each, filtering on the namespace
/// — about 30,000 symbols on BO3 — and it is asked once per namespace a file can see. That is the
/// same shape `DeclarationIndex` was built for, one question wider: a name identifies one or two
/// files, and a namespace identifies a few dozen, which is still nothing against the store.
///
/// Namespaces are FEW — hundreds across a game, against thousands of function names — so this keeps
/// a plain <c>HashSet</c> per key rather than the single-string-until-a-second-appears union
/// <see cref="DeclarationIndex"/> needs. The saving that union exists for does not apply when the
/// normal case is a key with many files.
/// </summary>
public sealed class NamespaceIndex
{
    private readonly Dictionary<string, HashSet<string>> _filesByNamespace = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// The distinct namespaces a function list declares into. Built outside the caller's write gate,
    /// like <see cref="DeclarationIndex.NamesOf"/>.
    /// </summary>
    public static HashSet<string> NamespacesOf(ImmutableArray<FunctionSymbol> functions)
    {
        HashSet<string> namespaces = new(StringComparer.Ordinal);
        foreach ( FunctionSymbol function in functions )
        {
            namespaces.Add(function.Namespace);
        }

        return namespaces;
    }

    /// <summary>Replaces one file's contribution: drops the namespaces it no longer declares into.</summary>
    public void Apply(string path, HashSet<string> oldNamespaces, HashSet<string> newNamespaces)
    {
        lock ( _gate )
        {
            foreach ( string name in oldNamespaces )
            {
                if ( newNamespaces.Contains(name) || !_filesByNamespace.TryGetValue(name, out HashSet<string>? files) )
                {
                    continue;
                }

                files.Remove(path);
                if ( files.Count == 0 )
                {
                    _filesByNamespace.Remove(name);
                }
            }

            foreach ( string name in newNamespaces )
            {
                if ( !_filesByNamespace.TryGetValue(name, out HashSet<string>? files) )
                {
                    files = new HashSet<string>(StringComparer.Ordinal);
                    _filesByNamespace[name] = files;
                }

                files.Add(path);
            }
        }
    }

    /// <summary>
    /// The files declaring a function in this namespace, or empty when nothing does.
    ///
    /// Snapshotted under the gate, so a caller can walk the result while indexing continues — the
    /// same contract the other two indexes offer.
    /// </summary>
    public ImmutableArray<string> FilesDeclaringInto(string namespaceName)
    {
        lock ( _gate )
        {
            if ( !_filesByNamespace.TryGetValue(namespaceName, out HashSet<string>? files) )
            {
                return [];
            }

            return [.. files];
        }
    }
}
