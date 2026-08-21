using GSCode.Workspace.Database;

namespace GSCode.Workspace.Cache;

/// <summary>
/// A cached record still in the form it was stored in: the freshness key, and the compressed blob
/// behind it. Turning the blob back into a <see cref="ScriptRecord"/> is deliberately NOT done when
/// the cache is read.
///
/// The reason is that most of the cost of a warm start is that conversion, and a good deal of it is
/// wasted. <see cref="SqliteCache.LoadAll"/> runs before the indexer knows which files are still
/// current, so materialising every record there pays gzip inflation and a JSON parse for files that
/// are about to be re-analysed anyway — and pays it on ONE thread, in front of an index that runs on
/// all of them. Handing the indexer the blob instead moves both halves into its parallel per-file
/// loop, behind the content-hash check that decides whether the record may be used at all.
/// </summary>
public sealed record CachedEntry(ulong ContentHash, byte[] Blob)
{
    /// <summary>Decompresses and parses the record, or null when the blob is unreadable.</summary>
    public ScriptRecord? Materialize()
    {
        return RecordSerializer.Deserialize(Blob);
    }
}
