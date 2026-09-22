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
        public static DeclaredKeys None { get; } = new([], []);

        public DeclaredKeys(HashSet<string> names, HashSet<(string Namespace, string KeyName)> qualified)
        {
            Names = names;
            Qualified = qualified;
        }

        public HashSet<string> Names { get; }

        public HashSet<(string Namespace, string KeyName)> Qualified { get; }
    }

    private readonly PackedInvertedIndex<string> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Keyed by the declared namespace exactly as <see cref="FunctionSymbol.Namespace"/> holds it,
    /// since that is what <see cref="DatabaseQueries.LookupFunctions"/> compares, ordinally.
    /// </summary>
    private readonly PackedInvertedIndex<(string Namespace, string KeyName)> _byQualifiedName =
        new(EqualityComparer<(string Namespace, string KeyName)>.Default);

    /// <summary>
    /// The distinct names, bare and qualified, a function list declares. Built outside the caller's
    /// write gate for the same reason as <see cref="ReferenceIndex.KeysOf"/>.
    /// </summary>
    public static DeclaredKeys KeysOf(ImmutableArray<FunctionSymbol> functions)
    {
        if ( functions.IsDefaultOrEmpty )
        {
            return DeclaredKeys.None;
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<(string Namespace, string KeyName)> qualified = [];
        foreach ( FunctionSymbol function in functions )
        {
            names.Add(function.KeyName);
            qualified.Add((function.Namespace, function.KeyName));
        }

        return new DeclaredKeys(names, qualified);
    }

    /// <summary>Replaces one file's contribution: removes names it no longer declares, adds the rest.</summary>
    public void Apply(string path, DeclaredKeys oldKeys, DeclaredKeys newKeys)
    {
        _byName.Apply(path, oldKeys.Names, newKeys.Names);
        _byQualifiedName.Apply(path, oldKeys.Qualified, newKeys.Qualified);
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
