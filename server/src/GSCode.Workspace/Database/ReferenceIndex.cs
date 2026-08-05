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
    private readonly Dictionary<SymbolKey, HashSet<string>> _filesByKey = [];
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
                if ( !newKeys.Contains(key) && _filesByKey.TryGetValue(key, out HashSet<string>? files) )
                {
                    files.Remove(path);
                    if ( files.Count == 0 )
                    {
                        _filesByKey.Remove(key);
                    }
                }
            }

            foreach ( SymbolKey key in newKeys )
            {
                if ( !_filesByKey.TryGetValue(key, out HashSet<string>? files) )
                {
                    files = new HashSet<string>(StringComparer.Ordinal);
                    _filesByKey[key] = files;
                }

                files.Add(path);
            }
        }
    }

    /// <summary>Paths of files mentioning the key (snapshot).</summary>
    public ImmutableArray<string> FilesFor(SymbolKey key)
    {
        lock ( _gate )
        {
            if ( !_filesByKey.TryGetValue(key, out HashSet<string>? files) )
            {
                return [];
            }

            return [.. files];
        }
    }
}
