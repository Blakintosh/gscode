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
///
/// The storage, the packing and the per-file diff live in <see cref="PackedInvertedIndex{TKey}"/>,
/// shared with <see cref="ReferenceIndex"/>.
///
/// It is kept TWICE: by bare name, and by namespace and name together. A namespaced lookup used to
/// read the bare-name list and throw away every file declaring into another namespace — fine while
/// a name was declared in a few dozen files, not once it is declared in thousands. Every bo3 system
/// file declares <c>__init__</c>, so in a 50,000-file workspace one <c>util::__init__</c> call walked
/// every one of them, and four lints asking that per call site made a single file's lint pass cost
/// more than the keystroke debounce (PERF.md, the scale section). The qualified list is exactly the
/// subset the namespace filter kept, so the answer is unchanged.
/// </summary>
public sealed class DeclarationIndex
{
    /// <summary>What one file contributes, built outside the caller's write gate.</summary>
    public sealed class DeclaredKeys
    {
        public static DeclaredKeys None { get; } = new([], [], []);

        public DeclaredKeys(
            HashSet<string> names, HashSet<(string Namespace, string KeyName)> qualified, HashSet<string> devOnlyNames)
        {
            Names = names;
            Qualified = qualified;
            DevOnlyNames = devOnlyNames;
        }

        public HashSet<string> Names { get; }

        public HashSet<(string Namespace, string KeyName)> Qualified { get; }

        /// <summary>Names of the functions AND class methods declared inside a dev block.</summary>
        public HashSet<string> DevOnlyNames { get; }
    }

    private readonly PackedInvertedIndex<string> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Keyed by the declared namespace exactly as <see cref="FunctionSymbol.Namespace"/> holds it,
    /// since that is what <see cref="DatabaseQueries.LookupFunctions"/> compares, ordinally.
    /// </summary>
    private readonly PackedInvertedIndex<(string Namespace, string KeyName)> _byQualifiedName =
        new(EqualityComparer<(string Namespace, string KeyName)>.Default);

    /// <summary>
    /// Every name that has a dev-only declaration somewhere — see <see cref="MayBeDevOnly"/>.
    /// Case-insensitive, so it can only ever answer "maybe" more often than an exact match would.
    /// </summary>
    private readonly PackedInvertedIndex<string> _devOnlyByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The distinct names, bare and qualified, a file's functions declare, and the dev-only ones
    /// among its functions and class methods. Built outside the caller's write gate for the same
    /// reason as <see cref="ReferenceIndex.KeysOf"/>.
    /// </summary>
    public static DeclaredKeys KeysOf(ScriptRecord? record)
    {
        if ( record is null || (record.Functions.IsDefaultOrEmpty && record.Classes.IsDefaultOrEmpty) )
        {
            return DeclaredKeys.None;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<(string Namespace, string KeyName)> qualified = [];
        HashSet<string> devOnlyNames = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in record.Functions )
        {
            names.Add(function.KeyName);
            qualified.Add((function.Namespace, function.KeyName));
            if ( function.IsDevOnly )
            {
                devOnlyNames.Add(function.KeyName);
            }
        }

        foreach ( ClassSymbol classSymbol in record.Classes )
        {
            foreach ( FunctionSymbol method in classSymbol.Methods )
            {
                if ( method.IsDevOnly )
                {
                    devOnlyNames.Add(method.KeyName);
                }
            }
        }

        return new DeclaredKeys(names, qualified, devOnlyNames);
    }

    /// <summary>Replaces one file's contribution: removes names it no longer declares, adds the rest.</summary>
    public void Apply(string path, DeclaredKeys oldKeys, DeclaredKeys newKeys)
    {
        _byName.Apply(path, oldKeys.Names, newKeys.Names);
        _byQualifiedName.Apply(path, oldKeys.Qualified, newKeys.Qualified);
        _devOnlyByName.Apply(path, oldKeys.DevOnlyNames, newKeys.DevOnlyNames);
    }

    /// <summary>
    /// Whether any file declares a function or class method of this name inside a dev block. False
    /// means no call to the name can reach a dev-only script declaration, however it resolves.
    /// </summary>
    public bool MayBeDevOnly(string keyName)
    {
        return !_devOnlyByName.FilesFor(keyName).IsEmpty;
    }

    /// <summary>
    /// The declared names beginning with <paramref name="lowercasePrefix"/>, in a file
    /// <paramref name="visible"/> accepts.
    ///
    /// The keys of this index ARE the names — lowercase, since that is what
    /// <see cref="FunctionSymbol.KeyName"/> holds — so a prefix query costs one pass over the
    /// distinct names rather than over the records declaring them, and the answer does not grow
    /// with the size of the workspace the way a scan of every record would. The same shape
    /// <c>VocabularyIndex</c> uses for literal and field completion.
    ///
    /// Only TOP-LEVEL functions are here (see <see cref="KeysOf"/>), which is the right set for the
    /// one caller: a class method is reached through an instance, never through an import.
    /// </summary>
    public List<string> NamesStartingWith(string lowercasePrefix, Func<string, bool> visible)
    {
        List<string> names = [];
        _byName.CollectKeys(key => key.StartsWith(lowercasePrefix, StringComparison.Ordinal), visible, names);
        return names;
    }

    /// <summary>Paths of the files declaring this key name in any namespace (snapshot).</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        return _byName.FilesFor(keyName);
    }

    /// <summary>Paths of the files declaring this key name into this namespace (snapshot).</summary>
    public ImmutableArray<string> FilesDeclaring(string namespaceName, string keyName)
    {
        return _byQualifiedName.FilesFor((namespaceName, keyName));
    }
}
