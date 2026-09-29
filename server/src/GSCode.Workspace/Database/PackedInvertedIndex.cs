using System.Collections.Immutable;

namespace GSCode.Workspace.Database;

/// <summary>
/// key → the files mentioning it, packed and sharded. The storage and the per-file diff that
/// <see cref="ReferenceIndex"/> and <see cref="DeclarationIndex"/> both need, written once.
///
/// <see cref="NamespaceIndex"/> is deliberately NOT built on this. It holds a plain
/// <c>HashSet</c> per key because a namespace is declared into by many files by nature, so the
/// packing below saves nothing there and the diff it needs is genuinely simpler. Unifying the
/// three would take a flag to tell the two shapes apart, which is the sign they are not one shape.
/// </summary>
/// <typeparam name="TKey">
/// What identifies an entry: a symbol key on the reference side, a lowercase-canonical name on the
/// declaration side.
/// </typeparam>
internal sealed class PackedInvertedIndex<TKey>
    where TKey : notnull
{
    /// <summary>
    /// Shard count. A power of two so the shard index is a mask rather than a division, and well
    /// above any realistic core count so two threads rarely meet on one shard. The whole cost is
    /// this many dictionaries and locks, allocated once.
    ///
    /// Sharding is what took <c>commit.upsert</c> from 28.6% of CoD4's cold-index thread-time to
    /// 8.4%: one dictionary under one lock meant a file's whole diff — thousands of keys on a large
    /// script — waited on every other indexing thread's. It is sound only because no invariant spans
    /// two keys: an entry is complete on its own, and nothing reads a group of them expecting one
    /// instant. See `PERF.md`.
    /// </summary>
    private const int ShardCount = 64;

    private const int ShardMask = ShardCount - 1;

    private sealed class Shard
    {
        /// <summary>
        /// key → the one file that mentions it, as a bare <c>string</c>, promoted to a
        /// <c>HashSet&lt;string&gt;</c> only once a second appears.
        ///
        /// The overwhelming majority of keys are mentioned by exactly one file, and a HashSet
        /// holding a single reference costs on the order of 150 bytes against the 8 of the
        /// reference itself. Measured on BO1, the largest corpus at 2,963 files, that is the
        /// difference between the declaration index costing 5.1 MB and costing well under one; the
        /// reference index is far larger again, since BO1 interns over a million references.
        ///
        /// The union is contained entirely within this class — nothing outside ever sees an
        /// <c>object</c>.
        /// </summary>
        public Dictionary<TKey, object> Entries = null!;

        public Lock Gate = null!;
    }

    private readonly Shard[] _shards;
    private readonly IEqualityComparer<TKey> _comparer;

    public PackedInvertedIndex(IEqualityComparer<TKey> comparer)
    {
        _comparer = comparer;
        _shards = new Shard[ShardCount];

        for ( int index = 0; index < _shards.Length; index++ )
        {
            _shards[index] = new Shard
            {
                Entries = new Dictionary<TKey, object>(comparer),
                Gate = new Lock(),
            };
        }
    }

    /// <summary>
    /// The shard owning a key. Uses the same comparer as the dictionaries, so a key that compares
    /// equal always lands on the shard holding it.
    /// </summary>
    private Shard ShardFor(TKey key)
    {
        return _shards[_comparer.GetHashCode(key) & ShardMask];
    }

    /// <summary>
    /// Replaces one file's contribution: removes the keys it no longer carries, adds the rest.
    ///
    /// One shard lock per key rather than one lock for the whole diff. An uncontended <c>Lock</c>
    /// costs a few nanoseconds and a file carries thousands of keys, so the acquisitions are
    /// microseconds against the milliseconds a single gate spent waiting.
    /// </summary>
    public void Apply(string path, HashSet<TKey> oldKeys, HashSet<TKey> newKeys)
    {
        foreach ( TKey key in oldKeys )
        {
            if ( newKeys.Contains(key) )
            {
                continue;
            }

            Shard shard = ShardFor(key);
            lock ( shard.Gate )
            {
                if ( !shard.Entries.TryGetValue(key, out object? existing) )
                {
                    continue;
                }

                if ( existing is HashSet<string> many )
                {
                    many.Remove(path);

                    // Not demoted back to a bare string on the way down: a key that has had two
                    // files usually gets them back, and this path runs on edits rather than on the
                    // cold index the memory shape is tuned for.
                    if ( many.Count == 0 )
                    {
                        shard.Entries.Remove(key);
                    }
                }
                else if ( string.Equals((string)existing, path, StringComparison.Ordinal) )
                {
                    shard.Entries.Remove(key);
                }
            }
        }

        foreach ( TKey key in newKeys )
        {
            Shard shard = ShardFor(key);
            lock ( shard.Gate )
            {
                if ( !shard.Entries.TryGetValue(key, out object? existing) )
                {
                    shard.Entries[key] = path;
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

                shard.Entries[key] = new HashSet<string>(StringComparer.Ordinal) { only, path };
            }
        }
    }

    /// <summary>Paths of the files carrying this key (a snapshot, safe to walk while indexing).</summary>
    public ImmutableArray<string> FilesFor(TKey key)
    {
        Shard shard = ShardFor(key);
        lock ( shard.Gate )
        {
            if ( !shard.Entries.TryGetValue(key, out object? existing) )
            {
                return [];
            }

            return existing is HashSet<string> many ? [.. many] : [(string)existing];
        }
    }

    /// <summary>
    /// Every key <paramref name="keyFilter"/> accepts that at least one file
    /// <paramref name="fileFilter"/> accepts carries — for the callers that want the KEYS
    /// (a vocabulary) rather than the files behind one of them.
    ///
    /// One shard at a time, each under its own gate, so a concurrent diff can land between shards:
    /// the answer is a snapshot per shard rather than of the whole index, which is all a completion
    /// list needs. <paramref name="fileFilter"/> runs under the gate, so it must take no lock of
    /// its own that a writer could hold while waiting on this one.
    /// </summary>
    public void CollectKeys(Func<TKey, bool> keyFilter, Func<string, bool> fileFilter, List<TKey> into)
    {
        Walk(keyFilter, fileFilter, (key, files) => into.Add(key));
    }

    /// <summary>
    /// <see cref="CollectKeys"/>, with how many files carry each key — ALL of them, not only the ones
    /// <paramref name="fileFilter"/> accepts. A ranking signal for a vocabulary list, where "how
    /// widely is this used" matters and counting only the visible files would mean testing every
    /// file of every key rather than stopping at the first visible one.
    /// </summary>
    public void CollectKeysWithFileCounts(
        Func<TKey, bool> keyFilter, Func<string, bool> fileFilter, List<(TKey Key, int Files)> into)
    {
        Walk(keyFilter, fileFilter, (key, files) => into.Add((key, files)));
    }

    private void Walk(Func<TKey, bool> keyFilter, Func<string, bool> fileFilter, Action<TKey, int> found)
    {
        foreach ( Shard shard in _shards )
        {
            lock ( shard.Gate )
            {
                foreach ( KeyValuePair<TKey, object> entry in shard.Entries )
                {
                    if ( !keyFilter(entry.Key) )
                    {
                        continue;
                    }

                    if ( entry.Value is HashSet<string> many )
                    {
                        foreach ( string path in many )
                        {
                            if ( fileFilter(path) )
                            {
                                found(entry.Key, many.Count);
                                break;
                            }
                        }
                    }
                    else if ( fileFilter((string)entry.Value) )
                    {
                        found(entry.Key, 1);
                    }
                }
            }
        }
    }
}
