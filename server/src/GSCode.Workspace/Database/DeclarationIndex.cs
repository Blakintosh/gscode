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
    private readonly Dictionary<string, HashSet<string>> _filesByName = new(StringComparer.Ordinal);
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
                if ( !newNames.Contains(name) && _filesByName.TryGetValue(name, out HashSet<string>? files) )
                {
                    files.Remove(path);
                    if ( files.Count == 0 )
                    {
                        _filesByName.Remove(name);
                    }
                }
            }

            foreach ( string name in newNames )
            {
                if ( !_filesByName.TryGetValue(name, out HashSet<string>? files) )
                {
                    files = new HashSet<string>(StringComparer.Ordinal);
                    _filesByName[name] = files;
                }

                files.Add(path);
            }
        }
    }

    /// <summary>Paths of the files declaring this key name (snapshot).</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        lock ( _gate )
        {
            if ( !_filesByName.TryGetValue(keyName, out HashSet<string>? files) )
            {
                return [];
            }

            return [.. files];
        }
    }
}
