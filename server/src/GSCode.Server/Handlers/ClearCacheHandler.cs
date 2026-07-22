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
/// The client used to do this itself, and got two things wrong that only a server-side
/// implementation can avoid:
///
/// 1. It recursively deleted the whole <c>gscode/cache</c> directory, discarding every other
///    workspace's cache as a side effect of reindexing one. The server knows the exact
///    <c>&lt;hash&gt;.db</c> for its own roots.
/// 2. It rebuilt the directory from <c>process.env.APPDATA</c> with a <c>??</c> fallback, which
///    only triggers on null/undefined — an APPDATA set but EMPTY produced the relative path
///    <c>gscode/cache</c>, resolved against the extension host's working directory and handed to
///    a recursive force delete.
///
/// There is also no drain handshake to get right here: the writer channel is completed and
/// awaited before the file is touched, rather than slept on for a fixed 300 ms.
/// </summary>
public sealed class ClearCacheHandler : IJsonRpcRequestHandler<ClearCacheParams, ClearCacheResponse>
{
    private readonly CacheHolder _cache;

    public ClearCacheHandler(CacheHolder cache)
    {
        _cache = cache;
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
