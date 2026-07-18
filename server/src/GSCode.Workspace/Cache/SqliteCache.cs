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

    private readonly SqliteConnection _connection;
    private readonly Channel<WriteCommand> _writes;
    private readonly Task _writerLoop;

    private SqliteCache(SqliteConnection connection)
    {
        _connection = connection;
        _writes = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(4096)
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

    /// <summary>Queues a record to persist (never blocks the caller for disk).</summary>
    public void Enqueue(ScriptRecord record)
    {
        _writes.Writer.TryWrite(new UpsertCommand(record));
    }

    /// <summary>Queues a file removal.</summary>
    public void EnqueueDelete(string normalizedPath)
    {
        _writes.Writer.TryWrite(new DeleteCommand(normalizedPath));
    }

    private async Task ProcessWritesAsync()
    {
        await foreach ( WriteCommand first in _writes.Reader.ReadAllAsync().ConfigureAwait(false) )
        {
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

        ReplaceDeps(record, transaction);
    }

    private void ReplaceDeps(ScriptRecord record, SqliteTransaction transaction)
    {
        using ( SqliteCommand clear = _connection.CreateCommand() )
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM deps WHERE path = $path;";
            clear.Parameters.AddWithValue("$path", record.Path);
            clear.ExecuteNonQuery();
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( edge.ResolvedPath.Length == 0 )
            {
                continue;
            }

            using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO deps (path, dep_path, is_insert)
                VALUES ($path, $dep, $insert);
                """;
            insert.Parameters.AddWithValue("$path", record.Path);
            insert.Parameters.AddWithValue("$dep", edge.ResolvedPath);
            insert.Parameters.AddWithValue("$insert", edge.IsInsert ? 1 : 0);
            insert.ExecuteNonQuery();
        }
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
