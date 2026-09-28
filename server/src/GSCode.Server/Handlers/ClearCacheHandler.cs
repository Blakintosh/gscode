using GSCode.Server.Configuration;
using GSCode.Workspace.Cache;
using MediatR;
using OmniSharp.Extensions.JsonRpc;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>Request for gscode/clearCache. No parameters: the server knows its own cache.</summary>
[Method("gscode/clearCache", Direction.ClientToServer)]
public sealed class ClearCacheParams : IRequest<ClearCacheResponse>
{
}

/// <summary>Response for gscode/clearCache.</summary>
public sealed class ClearCacheResponse
{
    /// <summary>True when a cache database was found and removed.</summary>
    public bool Deleted { get; set; }

    /// <summary>Empty on success, otherwise why nothing was deleted.</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// Deletes THIS workspace's cache database, so the next start reindexes from scratch.
///
/// Server-side because only the server knows the exact <c>&lt;hash&gt;.db</c> for its own roots.
/// Deleting the whole <c>gscode/cache</c> directory discards every other workspace's cache, and a
/// directory rebuilt from an EMPTY <c>APPDATA</c> is the relative path <c>gscode/cache</c>, resolved
/// against the extension host's working directory and handed to a recursive force delete.
///
/// The writer channel is completed and awaited before the file is touched, rather than slept on.
/// </summary>
public sealed class ClearCacheHandler : IJsonRpcRequestHandler<ClearCacheParams, ClearCacheResponse>
{
    private readonly CacheHolder _cache;
    private readonly IndexingLifetime _indexing;

    public ClearCacheHandler(CacheHolder cache, IndexingLifetime indexing)
    {
        _cache = cache;
        _indexing = indexing;
    }

    public async Task<ClearCacheResponse> Handle(ClearCacheParams request, CancellationToken cancellationToken)
    {
        string? databasePath = _cache.DatabasePath;
        if ( databasePath is null )
        {
            // Caching is off, or the cache failed to open. Nothing to delete, and the reindex the
            // client is about to trigger is still the right outcome.
            return new ClearCacheResponse { Message = "No workspace cache is open." };
        }

        // Stop the startup pass BEFORE closing the cache it may still be writing to: closed first,
        // it would enqueue into a completing channel and count every such write as dropped, for a
        // cache that no longer exists. The client reloads the window right after this either way
        // (see gscode.clearCacheAndReindex), so nothing this pass could still finish is useful.
        await _indexing.CancelAndWaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        // Drain and close first: SQLite holds the file, and the sidecars, until it is disposed.
        await _cache.CloseAsync().ConfigureAwait(false);

        bool deleted = SqliteCache.DeleteDatabase(databasePath);
        Log.Information("Cleared workspace cache {Path} (deleted: {Deleted})", databasePath, deleted);

        return new ClearCacheResponse
        {
            Deleted = deleted,
            Message = deleted ? "" : "The cache file was already absent or still locked.",
        };
    }
}
