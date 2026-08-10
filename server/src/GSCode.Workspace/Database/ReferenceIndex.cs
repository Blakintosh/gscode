using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// The inverted index: which files mention a symbol key. Answers are path sets only —
/// exact ranges come from scanning those files' (small) reference lists. One lock,
/// held per-file-diff only; reads snapshot under the same lock.
/// </summary>
public sealed class ReferenceIndex
{
    /// <summary>
    /// key → the file that mentions it, as a bare <c>string</c> while exactly one does and a
    /// <c>HashSet&lt;string&gt;</c> only once a second appears.
    ///
    /// The same shape <see cref="DeclarationIndex"/> uses, and for the same reason: most symbol keys
    /// are mentioned in a single file, and a HashSet holding one string reference costs on the order
    /// of 150 bytes to carry 8. This index is far larger than the declaration one — BO1 interns over
    /// a million references — so the saving is correspondingly bigger.
    /// </summary>
    private readonly Dictionary<SymbolKey, object> _filesByKey = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// The distinct keys a reference list mentions.
    ///
    /// Separate from <see cref="Apply"/> so the caller can build it OUTSIDE its own write gate. This
    /// walk is O(references in the file) — thousands for a large script — and it used to run while
    /// <c>LanguageStore._writeGate</c> was held, so every indexing thread waited on every other
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
        lock ( _gate )
        {
            foreach ( SymbolKey key in oldKeys )
            {
                if ( newKeys.Contains(key) || !_filesByKey.TryGetValue(key, out object? existing) )
                {
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Remove(path);
                    if ( many.Count == 0 )
                    {
                        _filesByKey.Remove(key);
                    }
                }
                else if ( string.Equals((string)existing, path, StringComparison.Ordinal) )
                {
                    _filesByKey.Remove(key);
                }
            }

            foreach ( SymbolKey key in newKeys )
            {
                if ( !_filesByKey.TryGetValue(key, out object? existing) )
                {
                    _filesByKey[key] = path;
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Add(path);
                    continue;
                }

                string only = (string)existing;
                if ( !string.Equals(only, path, StringComparison.Ordinal) )
                {
                    _filesByKey[key] = new HashSet<string>(StringComparer.Ordinal) { only, path };
                }
            }
        }
    }

    /// <summary>Paths of files mentioning the key (snapshot).</summary>
    public ImmutableArray<string> FilesFor(SymbolKey key)
    {
        lock ( _gate )
        {
            if ( !_filesByKey.TryGetValue(key, out object? existing) )
            {
                return [];
            }

            return existing is HashSet<string> many ? [.. many] : [(string)existing];
        }
    }
}
