using GSCode.Workspace.Cache;

namespace GSCode.Server.Configuration;

/// <summary>
/// Owns the persistent cache's lifetime, so handlers can reach it through DI.
///
/// The cache is opened during startup, after settings and workspace folders have arrived, which
/// is too late for constructor injection — hence a holder, matching <see cref="ResolverHolder"/>.
/// It also has to be closable on request rather than only at exit: clearing the cache means
/// draining the writer and releasing SQLite's handles before the file can be deleted.
/// </summary>
public sealed class CacheHolder
{
    private readonly object _gate = new();
    private SqliteCache? _cache;
    private string? _databasePath;

    /// <summary>The open cache, or null when caching is off or its open failed.</summary>
    public SqliteCache? Current
    {
        get
        {
            lock ( _gate )
            {
                return _cache;
            }
        }
    }

    /// <summary>
    /// Where the cache lives. Survives <see cref="CloseAsync"/>, because deleting the file is the
    /// step AFTER closing it.
    /// </summary>
    public string? DatabasePath
    {
        get
        {
            lock ( _gate )
            {
                return _databasePath;
            }
        }
    }

    public void Set(SqliteCache cache, string databasePath)
    {
        lock ( _gate )
        {
            _cache = cache;
            _databasePath = databasePath;
        }
    }

    /// <summary>
    /// Drains the write channel and closes the connection. Safe to call twice — exit runs it
    /// again after a clear, and the second call must not fault.
    /// </summary>
    public async ValueTask CloseAsync()
    {
        SqliteCache? cache;
        lock ( _gate )
        {
            cache = _cache;
            _cache = null;
        }

        if ( cache is not null )
        {
            await cache.DisposeAsync().ConfigureAwait(false);
        }
    }
}
