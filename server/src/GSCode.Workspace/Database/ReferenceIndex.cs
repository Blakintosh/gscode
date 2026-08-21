using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// The inverted index: which files mention a symbol key. Answers are path sets only —
/// exact ranges come from scanning those files' (small) reference lists.
///
/// SHARDED by key, so a diff of one file and a diff of another only collide on the keys they
/// actually share. It was one dictionary under one lock, which meant every indexing thread's whole
/// per-file diff — thousands of keys on a large script — waited on every other thread's. That, plus
/// the store-wide gate above it, is what made <c>commit.upsert</c> a quarter of CoD4's cold-index
/// thread-time at 20x parallelism. Reads shard the same way, which matters for the lint pass rather
/// than for indexing: <c>FilesFor</c> is on the hot path of the reference-based rules and used to
/// contend with whatever was being committed.
/// </summary>
public sealed class ReferenceIndex
{
    /// <summary>
    /// Shard count. Power of two so the shard index is a mask rather than a division, and well
    /// above any realistic core count so two threads rarely meet on one shard. The whole cost is
    /// this many dictionaries and locks, allocated once.
    /// </summary>
    private const int ShardCount = 64;

    private const int ShardMask = ShardCount - 1;

    /// <summary>
    /// key → the file that mentions it, as a bare <c>string</c> while exactly one does and a
    /// <c>HashSet&lt;string&gt;</c> only once a second appears.
    ///
    /// The same shape <see cref="DeclarationIndex"/> uses, and for the same reason: most symbol keys
    /// are mentioned in a single file, and a HashSet holding one string reference costs on the order
    /// of 150 bytes to carry 8. This index is far larger than the declaration one — BO1 interns over
    /// a million references — so the saving is correspondingly bigger.
    /// </summary>
    private sealed class Shard
    {
        public readonly Dictionary<SymbolKey, object> FilesByKey = [];
        public readonly Lock Gate = new();
    }

    private readonly Shard[] _shards = CreateShards();

    private static Shard[] CreateShards()
    {
        Shard[] shards = new Shard[ShardCount];
        for ( int index = 0; index < shards.Length; index++ )
        {
            shards[index] = new Shard();
        }

        return shards;
    }

    private Shard ShardFor(SymbolKey key)
    {
        return _shards[key.GetHashCode() & ShardMask];
    }

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

    /// <summary>
    /// Replaces one file's contribution: removes vanished keys, adds new ones.
    ///
    /// One shard lock per key rather than one lock for the diff. An uncontended <c>Lock</c> costs a
    /// few nanoseconds and a file carries thousands of keys, so the acquisitions are microseconds
    /// against the milliseconds the single gate was spending WAITING. The trade only works because
    /// no invariant spans two keys: a key's entry is complete on its own, and nothing reads a group
    /// of them expecting one instant.
    /// </summary>
    public void Apply(string path, HashSet<SymbolKey> oldKeys, HashSet<SymbolKey> newKeys)
    {
        foreach ( SymbolKey key in oldKeys )
        {
            if ( newKeys.Contains(key) )
            {
                continue;
            }

            Shard shard = ShardFor(key);
            lock ( shard.Gate )
            {
                if ( !shard.FilesByKey.TryGetValue(key, out object? existing) )
                {
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Remove(path);
                    if ( many.Count == 0 )
                    {
                        shard.FilesByKey.Remove(key);
                    }
                }
                else if ( string.Equals((string)existing, path, StringComparison.Ordinal) )
                {
                    shard.FilesByKey.Remove(key);
                }
            }
        }

        foreach ( SymbolKey key in newKeys )
        {
            Shard shard = ShardFor(key);
            lock ( shard.Gate )
            {
                if ( !shard.FilesByKey.TryGetValue(key, out object? existing) )
                {
                    shard.FilesByKey[key] = path;
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
                    shard.FilesByKey[key] = new HashSet<string>(StringComparer.Ordinal) { only, path };
                }
            }
        }
    }

    /// <summary>Paths of files mentioning the key (snapshot).</summary>
    public ImmutableArray<string> FilesFor(SymbolKey key)
    {
        Shard shard = ShardFor(key);
        lock ( shard.Gate )
        {
            if ( !shard.FilesByKey.TryGetValue(key, out object? existing) )
            {
                return [];
            }

            return existing is HashSet<string> many ? [.. many] : [(string)existing];
        }
    }
}
