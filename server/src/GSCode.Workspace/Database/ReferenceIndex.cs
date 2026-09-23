using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// The inverted index: which files mention a symbol key. Answers are path sets only —
/// exact ranges come from scanning those files' (small) reference lists.
///
/// The storage, the packing and the per-file diff live in <see cref="PackedInvertedIndex{TKey}"/>,
/// which <see cref="DeclarationIndex"/> shares. What is here is the reference side's own vocabulary
/// and the walk that turns a record's reference list into keys.
/// </summary>
public sealed class ReferenceIndex
{
    private readonly PackedInvertedIndex<SymbolKey> _index = new(EqualityComparer<SymbolKey>.Default);

    /// <summary>
    /// The distinct keys a reference list mentions.
    ///
    /// Separate from <see cref="Apply"/> so the caller can build it OUTSIDE its own write gate. This
    /// walk is O(references in the file) — thousands for a large script — and it used to run while
    /// <c>LanguageStore</c>'s write gate was held, so every indexing thread waited on every other
    /// thread's hashing. That made the commit stage 22% of CoD4's cold-index thread-time.
    /// </summary>
    public static HashSet<SymbolKey> KeysOf(ImmutableArray<ReferenceEntry> references)
    {
        HashSet<SymbolKey> keys = [];
        foreach ( ReferenceEntry entry in references )
        {
            keys.Add(entry.Key);
        }

        return keys;
    }

    /// <summary>Replaces one file's contribution: removes vanished keys, adds new ones.</summary>
    public void Apply(string path, HashSet<SymbolKey> oldKeys, HashSet<SymbolKey> newKeys)
    {
        _index.Apply(path, oldKeys, newKeys);
    }

    /// <summary>Paths of files mentioning the key (snapshot).</summary>
    public ImmutableArray<string> FilesFor(SymbolKey key)
    {
        return _index.FilesFor(key);
    }

    /// <summary>
    /// Every FUNCTION key sharing a bare name, whatever namespace or owning class it carries.
    ///
    /// For the one question that is about a name rather than about a key: an engine builtin has no
    /// declaration, so its call sites are keyed by whatever scope each was WRITTEN in — the file's
    /// namespace, the enclosing class, or nothing at all for `sys::`. Reading the index's KEY SET
    /// rather than its records keeps that bounded by how many distinct scopes mention the name, not
    /// by how many files exist; it is the shape `DeclarationIndex.NamesStartingWith` already uses
    /// for completion.
    /// </summary>
    public List<SymbolKey> KeysNamed(string name)
    {
        List<SymbolKey> keys = [];
        _index.CollectKeys(
            key => key.Kind == SymbolKind.Function && string.Equals(key.Name, name, StringComparison.Ordinal),
            static _ => true,
            keys);

        return keys;
    }
}
