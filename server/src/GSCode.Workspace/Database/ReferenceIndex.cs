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

    /// <summary>Replaces one file's contribution: removes vanished keys, adds new ones.</summary>
    public void Apply(string path, ImmutableArray<ReferenceEntry> oldReferences, ImmutableArray<ReferenceEntry> newReferences)
    {
        HashSet<SymbolKey> oldKeys = [];
        foreach ( ReferenceEntry entry in oldReferences )
        {
            oldKeys.Add(entry.Key);
        }

        HashSet<SymbolKey> newKeys = [];
        foreach ( ReferenceEntry entry in newReferences )
        {
            newKeys.Add(entry.Key);
        }

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
