using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using Microsoft.Data.Sqlite;

namespace GSCode.Workspace.Cache;

/// <summary>
/// Per-workspace SQLite cache of analysed script records. A single background writer
/// drains a bounded channel, so analysis threads never block on disk. Any version or
/// server-identity mismatch wipes the cache on open — no migrations. Records are stored
/// as gzipped JSON blobs; cold start loads them all and re-parses only stale files.
/// </summary>
public sealed class SqliteCache : IAsyncDisposable
{
    private abstract record WriteCommand;
    private sealed record UpsertCommand(ScriptRecord Record) : WriteCommand;
    private sealed record DeleteCommand(string Path) : WriteCommand;

    /// <summary>
    /// How many pending writes the queue holds before it starts refusing them. Named because the
    /// number is a trade — larger costs memory holding records the writer has not reached, smaller
    /// drops entries sooner under a backlog. See <see cref="Enqueue"/>.
    /// </summary>
    private const int WriteQueueCapacity = 4096;

    private readonly SqliteConnection _connection;
    private readonly Channel<WriteCommand> _writes;
    private readonly Task _writerLoop;

    private SqliteCache(SqliteConnection connection)
    {
        _connection = connection;
        _writes = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(WriteQueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _writerLoop = Task.Run(ProcessWritesAsync);
    }

    /// <summary>The location of a workspace's cache DB: %APPDATA%/gscode/cache/&lt;hash&gt;.db.</summary>
    public static string ResolveDatabasePath(IEnumerable<string> workspaceRoots)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string cacheDir = Path.Combine(appData, "gscode", "cache");
        Directory.CreateDirectory(cacheDir);

        string joined = string.Join('\n', workspaceRoots.OrderBy(static root => root, StringComparer.Ordinal));
        byte[] digest = SHA1.HashData(Encoding.UTF8.GetBytes(joined));
        string hash = Convert.ToHexString(digest)[..16].ToLowerInvariant();

        return Path.Combine(cacheDir, hash + ".db");
    }

    /// <summary>
    /// Deletes ONE workspace's cache database, plus the -wal/-shm sidecars SQLite leaves beside
    /// it. Call after <see cref="DisposeAsync"/>, so the writer has drained and the handles are
    /// released.
    ///
    /// Scoped to a single file on purpose. The client used to do this by recursively deleting the
    /// whole `gscode/cache` directory, which threw away every other workspace's cache as a side
    /// effect of reindexing one — and computed that directory from `process.env.APPDATA`, which
    /// yields a RELATIVE path when the variable is set but empty, pointing a recursive force
    /// delete at whatever the extension host's working directory happened to be.
    /// </summary>
    /// <returns>True when a database file was found and removed.</returns>
    public static bool DeleteDatabase(string databasePath)
    {
        // A relative path here would resolve against the process's working directory, which is
        // never where a cache lives. Refusing is the only safe response to a malformed path.
        if ( databasePath.Length == 0 || !Path.IsPathFullyQualified(databasePath) )
        {
            return false;
        }

        bool deleted = false;

        foreach ( string suffix in new[] { "", "-wal", "-shm" } )
        {
            string path = databasePath + suffix;
            try
            {
                if ( File.Exists(path) )
                {
                    File.Delete(path);
                    deleted |= suffix.Length == 0;
                }
            }
            catch ( IOException )
            {
                // Still held, or gone already. The cache is a rebuildable artifact, so a failure
                // to remove it costs a stale-looking reindex rather than correctness.
            }
            catch ( UnauthorizedAccessException )
            {
            }
        }

        return deleted;
    }

    /// <summary>Deletes the legacy single-file gzip-JSON cache from the old server, if present.</summary>
    public static void CleanUpLegacyCache()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string legacy = Path.Combine(appData, "gscode", "cache.db");
        try
        {
            if ( File.Exists(legacy) )
            {
                File.Delete(legacy);
            }
        }
        catch ( IOException )
        {
            // A locked legacy file is harmless; ignore.
        }
    }

    /// <summary>
    /// Opens (or creates) the cache. On any version or build-identity mismatch the file
    /// table is wiped so cold start re-indexes from scratch.
    /// </summary>
    public static SqliteCache Open(string databasePath, string serverBuildIdentity)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, CacheSchema.CreateTables);

        if ( !IsCurrent(connection, serverBuildIdentity) )
        {
            Execute(connection, "DELETE FROM files; DELETE FROM deps; DELETE FROM meta;");
            WriteMeta(connection, serverBuildIdentity);
        }

        return new SqliteCache(connection);
    }

    /// <summary>Reads every cached record (cold-restore input), skipping any that fail to deserialize.</summary>
    public IReadOnlyDictionary<string, ScriptRecord> LoadAll()
    {
        Dictionary<string, ScriptRecord> records = new(StringComparer.Ordinal);

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT path, record FROM files;";
        using SqliteDataReader reader = command.ExecuteReader();

        while ( reader.Read() )
        {
            string path = reader.GetString(0);
            byte[] blob = (byte[])reader[1];
            ScriptRecord? record = RecordSerializer.Deserialize(blob);
            if ( record is not null )
            {
                records[path] = record;
            }
        }

        return records;
    }

    /// <summary>
    /// Records the channel refused, which is the difference between a warm start and a warm start
    /// that quietly re-analyses part of the workspace.
    /// </summary>
    public int DroppedWrites
    {
        get { return Volatile.Read(ref _dropped); }
    }

    private int _dropped;

    /// <summary>
    /// Queues a record to persist (never blocks the caller for disk).
    ///
    /// <c>TryWrite</c> is the right call here — this runs on the indexing threads and must not block
    /// on disk — but its RESULT used to be discarded. The channel is bounded at
    /// <see cref="WriteQueueCapacity"/>, and <c>BoundedChannelFullMode.Wait</c> only applies to
    /// <c>WriteAsync</c>: on a full channel <c>TryWrite</c> returns false and the record is simply
    /// gone. With N analysis threads feeding one writer that also serializes and gzips inline, a
    /// backlog past the bound is reachable on a large corpus — and the only symptom was a later
    /// "warm" start silently re-analysing those files.
    ///
    /// Counted rather than blocked: dropping a cache entry costs one file's re-analysis next start,
    /// while blocking an indexing thread on disk costs the whole index. The count is reported when
    /// indexing finishes, so it can never be silent again.
    /// </summary>
    public void Enqueue(ScriptRecord record)
    {
        if ( !_writes.Writer.TryWrite(new UpsertCommand(record)) )
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Queues a file removal.</summary>
    public void EnqueueDelete(string normalizedPath)
    {
        if ( !_writes.Writer.TryWrite(new DeleteCommand(normalizedPath)) )
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Completes once the writer has nothing left to do.
    ///
    /// The caller that wants this is the post-index settle step. Indexing hands thousands of records
    /// to a single writer that serializes and gzips each one, so the writer is still going long after
    /// IndexAsync returns — and compacting the heap while it works measures a moment that is about to
    /// be undone, which is exactly the "memory drops then climbs again" the server used to report.
    ///
    /// Polling rather than a signal, deliberately: the alternative is a counter mutated by every
    /// producer thread on the indexing hot path, and this is called once per index by one caller
    /// that is already waiting.
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while ( !cancellationToken.IsCancellationRequested )
        {
            if ( _writes.Reader.Count == 0 && Volatile.Read(ref _writing) == 0 )
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private int _writing;

    private async Task ProcessWritesAsync()
    {
        await foreach ( WriteCommand first in _writes.Reader.ReadAllAsync().ConfigureAwait(false) )
        {
            Interlocked.Exchange(ref _writing, 1);

            // Coalesce whatever else is queued into one transaction for throughput.
            List<WriteCommand> batch = [first];
            while ( _writes.Reader.TryRead(out WriteCommand? next) )
            {
                batch.Add(next);
                if ( batch.Count >= 512 )
                {
                    break;
                }
            }

            try
            {
                ApplyBatch(batch);
            }
            catch ( SqliteException )
            {
                // A failed cache write must never take the server down; the file will
                // simply be re-analysed next cold start.
            }
            finally
            {
                Interlocked.Exchange(ref _writing, 0);
            }
        }
    }

    private void ApplyBatch(List<WriteCommand> batch)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();

        foreach ( WriteCommand command in batch )
        {
            switch ( command )
            {
                case UpsertCommand upsert:
                    ApplyUpsert(upsert.Record, transaction);
                    break;
                case DeleteCommand delete:
                    ApplyDelete(delete.Path, transaction);
                    break;
                default:
                    break;
            }
        }

        transaction.Commit();
    }

    private void ApplyUpsert(ScriptRecord record, SqliteTransaction transaction)
    {
        // Unsaved editor state is never persisted.
        if ( record.IsDirty )
        {
            return;
        }

        using ( SqliteCommand upsert = _connection.CreateCommand() )
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO files (path, language, context_id, relative, content_hash, analysed_at, record)
                VALUES ($path, $language, $context, $relative, $hash, $at, $record)
                ON CONFLICT(path) DO UPDATE SET
                    language = excluded.language,
                    context_id = excluded.context_id,
                    relative = excluded.relative,
                    content_hash = excluded.content_hash,
                    analysed_at = excluded.analysed_at,
                    record = excluded.record;
                """;
            upsert.Parameters.AddWithValue("$path", record.Path);
            upsert.Parameters.AddWithValue("$language", (int)record.Language);
            upsert.Parameters.AddWithValue("$context", record.ContextId);
            upsert.Parameters.AddWithValue("$relative", record.RelativePath);
            upsert.Parameters.AddWithValue("$hash", record.ContentHash.ToString());
            upsert.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            upsert.Parameters.AddWithValue("$record", RecordSerializer.Serialize(record));
            upsert.ExecuteNonQuery();
        }

        // The `deps` table is deliberately NOT written. Nothing reads it: the same dependency edges
        // travel inside the serialized record (ScriptRecord.Dependencies), and that is what the
        // indexer's phase two uses to find files whose headers changed. Writing it cost a DELETE
        // plus one freshly-built SqliteCommand per edge per file — new command object, new parameter
        // collection, SQL re-parsed each time — plus maintaining ix_deps_dep, for rows no query ever
        // selected. The table stays in the schema so an existing database still opens.
    }

    private void ApplyDelete(string path, SqliteTransaction transaction)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM files WHERE path = $path; DELETE FROM deps WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static bool IsCurrent(SqliteConnection connection, string serverBuildIdentity)
    {
        string? schema = ReadMeta(connection, CacheSchema.MetaSchemaVersion);
        string? format = ReadMeta(connection, CacheSchema.MetaRecordFormatVersion);
        string? identity = ReadMeta(connection, CacheSchema.MetaServerBuildIdentity);

        return schema == CacheSchema.SchemaVersion.ToString()
            && format == CacheSchema.RecordFormatVersion.ToString()
            && identity == serverBuildIdentity;
    }

    private static void WriteMeta(SqliteConnection connection, string serverBuildIdentity)
    {
        SetMeta(connection, CacheSchema.MetaSchemaVersion, CacheSchema.SchemaVersion.ToString());
        SetMeta(connection, CacheSchema.MetaRecordFormatVersion, CacheSchema.RecordFormatVersion.ToString());
        SetMeta(connection, CacheSchema.MetaServerBuildIdentity, serverBuildIdentity);
    }

    private static string? ReadMeta(SqliteConnection connection, string key)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Drains the writer queue, closes the connection. A killed server loses only in-flight rows.</summary>
    public async ValueTask DisposeAsync()
    {
        _writes.Writer.TryComplete();
        await _writerLoop.ConfigureAwait(false);

        Execute(_connection, "PRAGMA wal_checkpoint(FULL);");
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
