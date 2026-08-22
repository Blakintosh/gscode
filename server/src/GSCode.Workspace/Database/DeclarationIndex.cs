using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// Which files DECLARE a function name — the counterpart to <see cref="ReferenceIndex"/>, which
/// answers which files mention one.
///
/// It exists because <see cref="DatabaseQueries.LookupFunctions"/> asked the question by walking
/// every record and every function in each — around thirty thousand symbols on BO3 — once per CALL
/// SITE. A file with two hundred calls scanned the whole store two hundred times, and four lints
/// doing that were 97% of the cross-file lint cost, itself some twenty times the parse it runs on.
///
/// Keyed by <see cref="FunctionSymbol.KeyName"/>, the lowercase-canonical form, compared ordinally
/// — exactly the comparison the lookup it replaces performs, so the candidate set is identical and
/// every filter that follows (visibility, namespace, privacy, overlay shadowing) is untouched. This
/// narrows WHERE to look; it decides nothing.
///
/// Paths rather than records, matching <see cref="ReferenceIndex"/>: a record is swapped wholesale
/// on every edit, so holding one here would pin a stale version. The caller resolves the path
/// through the record map it was going to read anyway.
/// </summary>
public sealed class DeclarationIndex
{
    /// <summary>
    /// name → the file that declares it, as a bare <c>string</c> when exactly one does and a
    /// <c>HashSet&lt;string&gt;</c> only once a second appears.
    ///
    /// The overwhelming majority of function names are declared exactly once, and a HashSet holding
    /// a single reference costs on the order of 150 bytes against the 8 of the reference itself.
    /// Measured on BO1, the largest corpus at 2,963 files, this is the difference between the index
    /// costing 5.1 MB and costing well under one.
    ///
    /// The union type is contained entirely within this class: nothing outside sees an
    /// <c>object</c>, and both shapes are read through <see cref="FilesDeclaring"/>.
    /// </summary>
    private readonly Dictionary<string, object> _filesByName = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// The distinct key names a function list declares. Built outside the caller's write gate for
    /// the same reason as <see cref="ReferenceIndex.KeysOf"/>.
    /// </summary>
    public static HashSet<string> NamesOf(ImmutableArray<FunctionSymbol> functions)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach ( FunctionSymbol function in functions )
        {
            names.Add(function.KeyName);
        }

        return names;
    }

    /// <summary>Replaces one file's contribution: removes names it no longer declares, adds the rest.</summary>
    public void Apply(string path, HashSet<string> oldNames, HashSet<string> newNames)
    {
        lock ( _gate )
        {
            foreach ( string name in oldNames )
            {
                if ( newNames.Contains(name) || !_filesByName.TryGetValue(name, out object? existing) )
                {
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Remove(path);

                    // Back down to one: keep the set rather than churning it back into a string. A
                    // name that has had two declarers usually gets them back, and this path runs on
                    // edits rather than on the cold index that the memory shape is tuned for.
                    if ( many.Count == 0 )
                    {
                        _filesByName.Remove(name);
                    }
                }
                else if ( string.Equals((string)existing, path, StringComparison.Ordinal) )
                {
                    _filesByName.Remove(name);
                }
            }

            foreach ( string name in newNames )
            {
                if ( !_filesByName.TryGetValue(name, out object? existing) )
                {
                    _filesByName[name] = path;
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Add(path);
                    continue;
                }

                string only = (string)existing;
                if ( string.Equals(only, path, StringComparison.Ordinal) )
                {
                    continue;
                }

                _filesByName[name] = new HashSet<string>(StringComparer.Ordinal) { only, path };
            }
        }
    }

    /// <summary>Paths of the files declaring this key name (snapshot).</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        lock ( _gate )
        {
            if ( !_filesByName.TryGetValue(keyName, out object? existing) )
            {
                return [];
            }

            return existing is HashSet<string> many ? [.. many] : [(string)existing];
        }
    }
}
