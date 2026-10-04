using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using GSCode.Workspace.Database;
using Microsoft.Data.Sqlite;
using System.Globalization;

namespace GSCode.Workspace.Cache;

/// <summary>
/// Per-workspace SQLite cache of analysed script records. Records are serialized on the thread
/// that enqueues them and a single background writer persists the blobs, so analysis threads
/// never block on disk. Any version or
/// server-identity mismatch wipes the cache on open — no migrations. Records are stored
/// as deflated binary blobs (see RecordSerializer); cold start loads them all and re-parses only
/// stale files.
/// </summary>
public sealed class SqliteCache : IAsyncDisposable
{
    private abstract record WriteCommand;
    private sealed record UpsertCommand(
        string Path, int Language, string ContextId, string RelativePath, ulong ContentHash, byte[] Blob) : WriteCommand;
    private sealed record DeleteCommand(string Path) : WriteCommand;

    private readonly SqliteConnection _connection;
    private readonly Channel<WriteCommand> _writes;
    private readonly Task _writerLoop;

    private SqliteCache(SqliteConnection connection)
    {
        _connection = connection;
        // Unbounded, deliberately — see Enqueue for why a bound here loses writes. Not marked
        // SingleReader although there is one reader: turning it on is a throughput change with no
        // measurement behind it.
        _writes = Channel.CreateUnbounded<WriteCommand>();
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
    /// Scoped to a single file on purpose: deleting the whole `gscode/cache` directory throws away every
    /// other workspace's cache, and a directory computed from an empty `APPDATA` is RELATIVE, pointing
    /// a recursive force delete at whatever the extension host's working directory happens to be.
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
            catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
            {
                // Still held, gone already, or not ours to delete. The cache is a rebuildable
                // artifact, so a failure to remove it costs a stale-looking reindex rather than
                // correctness.
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

            // Microsoft.Data.Sqlite pools connections by default: DisposeAsync would return this
            // one to the pool rather than closing its OS file handle, leaving the database file
            // locked on Windows. There is exactly one connection per SqliteCache and it is never
            // reopened, so pooling buys nothing here — only the surprise that DeleteDatabase can
            // fail right after DisposeAsync.
            Pooling = false,
        }.ToString());
        connection.Open();

        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA busy_timeout=5000;");
        Execute(connection, CacheSchema.CreateTables);

        if ( !IsCurrent(connection, serverBuildIdentity) )
        {
            Execute(connection, "DROP TABLE IF EXISTS deps; DELETE FROM files; DELETE FROM meta;");
            WriteMeta(connection, serverBuildIdentity);
        }

        return new SqliteCache(connection);
    }

    /// <summary>
    /// Reads every cached entry (warm-restore input) WITHOUT deserializing any of them.
    ///
    /// Deserializing here would make this the whole cost of a warm start: one thread inflating every
    /// record before indexing starts, outside the stopwatch that times indexing (the server calls this
    /// as an ARGUMENT to <c>UseCache</c>). Measured against a cold index of the same tree: bo3 1,509 ms
    /// against 390, cod4 720 against 236, bo1 2,747 against 718 — a serial restore four times slower
    /// than the <c>ProcessorCount - 1</c> analysis it exists to avoid.
    ///
    /// What is left here is the part that has to be serial: <see cref="SqliteDataReader"/> is not
    /// thread-safe and a blob is only valid until the next <c>Read</c>. That part is cheap, because
    /// the bytes are still compressed. The expensive part now happens inside the indexer's parallel
    /// per-file loop and only for files whose content hash still matches — see
    /// <see cref="CachedEntry"/>.
    ///
    /// The hash is stored beside the blob rather than inside it for exactly this reason: the
    /// freshness check has to be answerable without paying for the record it guards.
    /// </summary>
    public IReadOnlyDictionary<string, CachedEntry> LoadAll()
    {
        Dictionary<string, CachedEntry> entries = new(StringComparer.Ordinal);

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT path, content_hash, record FROM files;";
        using SqliteDataReader reader = command.ExecuteReader();

        while ( reader.Read() )
        {
            // Written as text by the upsert, since SQLite's INTEGER is signed and a content hash is
            // not. A row whose hash cannot be read is simply not offered for restore: it would fail
            // the freshness check anyway, and re-analysing one file is the cheap outcome.
            if ( !ulong.TryParse(reader.GetString(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong contentHash) )
            {
                continue;
            }

            entries[reader.GetString(0)] = new CachedEntry(contentHash, (byte[])reader[2]);
        }

        return entries;
    }

    /// <summary>
    /// Records the channel refused, which is the difference between a warm start and a warm start
    /// that quietly re-analyses part of the workspace. With an unbounded channel that is only a
    /// write arriving after <see cref="DisposeAsync"/> closed it.
    /// </summary>
    public int DroppedWrites
    {
        get { return Volatile.Read(ref _dropped); }
    }

    private int _dropped;

    /// <summary>
    /// Serializes a record and queues it to persist. Never blocks the caller on disk, and never
    /// refuses a write because the writer is behind.
    ///
    /// The serializing happens HERE, on the enqueuing thread, so it runs across every indexing core and
    /// leaves the writer only the SQL — which keeps the backlog small. And the channel is unbounded,
    /// so a backlog that does build (a slow disk, an antivirus scan of the database) costs memory rather
    /// than data: bounded at 4,096 and fed with <c>TryWrite</c>, it refused 40,889 writes at 50,000
    /// files (PERF.md, the scale section) and the next start re-analysed four files in five while
    /// reporting itself warm. What it holds is bounded anyway — the compressed blobs, about 7 KB a
    /// file, only until the writer reaches them.
    ///
    /// Dirty records are skipped before paying for a serialize — unsaved editor state is never
    /// persisted.
    /// </summary>
    public void Enqueue(ScriptRecord record)
    {
        if ( record.IsDirty )
        {
            return;
        }

        UpsertCommand command = new(
            record.Path,
            (int)record.Language,
            record.ContextId,
            record.RelativePath,
            record.ContentHash,
            RecordSerializer.Serialize(record));

        Submit(command);
    }

    /// <summary>Queues a file removal.</summary>
    public void EnqueueDelete(string normalizedPath)
    {
        Submit(new DeleteCommand(normalizedPath));
    }

    /// <summary>
    /// Hands one command to the writer, counting it as outstanding BEFORE it becomes visible to
    /// the reader.
    ///
    /// The order is the whole point. <see cref="_pending"/> is incremented first, so there is no
    /// instant in which a command is readable but uncounted, the instant an idle test could observe.
    /// If the channel refuses the write (only possible once
    /// <see cref="DisposeAsync"/> has closed it) the count is given back, because nothing will ever
    /// process it.
    /// </summary>
    private void Submit(WriteCommand command)
    {
        Interlocked.Increment(ref _pending);

        if ( !_writes.Writer.TryWrite(command) )
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>
    /// Completes once the writer has nothing left to do.
    ///
    /// The caller that wants this is the post-index settle step. Indexing hands thousands of blobs to
    /// a single writer, so the writer can still be going after IndexAsync returns — and compacting the
    /// heap while it works measures a moment that is about to be undone.
    ///
    /// Polling rather than a signal, deliberately: this is called once per index by one caller that
    /// is already waiting, so a signal would buy nothing it can use.
    ///
    /// What it polls is ONE counter of outstanding commands, incremented before a command reaches
    /// the channel and decremented only after its transaction has committed. The channel's Count plus
    /// a writer flag would have a hole: the writer takes a command OFF the channel — Count drops to
    /// zero — before raising the flag, so a poll landing there returns idle with a write in flight
    /// (`CacheRowPruningTests` caught that at about one run in seven, under a loaded thread pool).
    ///
    /// Every producer mutates the counter: one `Interlocked.Increment` beside `TryWrite`'s own
    /// interlocked bookkeeping, on a path that then serializes and compresses a record.
    /// </summary>
    public async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while ( !cancellationToken.IsCancellationRequested )
        {
            if ( Volatile.Read(ref _pending) == 0 )
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Commands handed to the writer that have not yet been committed — queued or in flight.
    /// See <see cref="WaitForIdleAsync"/> for why in-flight has to be part of the same number.
    /// </summary>
    private int _pending;

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
            catch ( Exception exception ) when ( exception is not OutOfMemoryException )
            {
                // A failed cache write must never take the server down; the file will simply be
                // re-analysed next cold start. Any exception, not just SqliteException: one escaping
                // would end the `await foreach`, nothing would drain the channel again, and every
                // later write would pile up dropped for the rest of the session.
            }
            finally
            {
                // After the transaction, not before: a waiter must not see zero until what it is
                // waiting for is readable. In the `catch` above the batch is lost on purpose, and
                // its count still has to come off or nothing would ever look idle again.
                Interlocked.Add(ref _pending, -batch.Count);
            }
        }
    }

    /// <summary>
    /// Applies one coalesced batch — up to 512 commands — inside a single transaction.
    ///
    /// The two statements are built ONCE per batch and reused, with only their parameter values
    /// reassigned per record. Building them per record meant a fresh SqliteCommand, a fresh
    /// parameter collection and seven boxed values every time, for every file of a cold index.
    /// </summary>
    private void ApplyBatch(List<WriteCommand> batch)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand upsert = CreateUpsertCommand(transaction);
        using SqliteCommand delete = CreateDeleteCommand(transaction);

        foreach ( WriteCommand command in batch )
        {
            switch ( command )
            {
                case UpsertCommand upsertCommand:
                    ApplyUpsert(upsertCommand, upsert);
                    break;
                case DeleteCommand deleteCommand:
                    ApplyDelete(deleteCommand.Path, delete);
                    break;
                default:
                    break;
            }
        }

        transaction.Commit();
    }

    private SqliteCommand CreateUpsertCommand(SqliteTransaction transaction)
    {
        SqliteCommand upsert = _connection.CreateCommand();
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

        // Added once with a placeholder; ApplyUpsert assigns Value per record.
        upsert.Parameters.AddWithValue("$path", "");
        upsert.Parameters.AddWithValue("$language", 0);
        upsert.Parameters.AddWithValue("$context", "");
        upsert.Parameters.AddWithValue("$relative", "");
        upsert.Parameters.AddWithValue("$hash", "");
        upsert.Parameters.AddWithValue("$at", 0L);
        upsert.Parameters.AddWithValue("$record", Array.Empty<byte>());
        return upsert;
    }

    private SqliteCommand CreateDeleteCommand(SqliteTransaction transaction)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", "");
        return command;
    }

    private static void ApplyUpsert(UpsertCommand command, SqliteCommand upsert)
    {
        upsert.Parameters["$path"].Value = command.Path;
        upsert.Parameters["$language"].Value = command.Language;
        upsert.Parameters["$context"].Value = command.ContextId;
        upsert.Parameters["$relative"].Value = command.RelativePath;
        upsert.Parameters["$hash"].Value = command.ContentHash.ToString(CultureInfo.InvariantCulture);
        upsert.Parameters["$at"].Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        upsert.Parameters["$record"].Value = command.Blob;
        upsert.ExecuteNonQuery();
    }

    private static void ApplyDelete(string path, SqliteCommand command)
    {
        command.Parameters["$path"].Value = path;
        command.ExecuteNonQuery();
    }

    private static bool IsCurrent(SqliteConnection connection, string serverBuildIdentity)
    {
        string? schema = ReadMeta(connection, CacheSchema.MetaSchemaVersion);
        string? format = ReadMeta(connection, CacheSchema.MetaRecordFormatVersion);
        string? identity = ReadMeta(connection, CacheSchema.MetaServerBuildIdentity);

        return schema == CacheSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture)
            && format == CacheSchema.RecordFormatVersion.ToString(CultureInfo.InvariantCulture)
            && identity == serverBuildIdentity;
    }

    private static void WriteMeta(SqliteConnection connection, string serverBuildIdentity)
    {
        SetMeta(connection, CacheSchema.MetaSchemaVersion, CacheSchema.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        SetMeta(connection, CacheSchema.MetaRecordFormatVersion, CacheSchema.RecordFormatVersion.ToString(CultureInfo.InvariantCulture));
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

        try
        {
            // Awaiting the writer is the one place a truly unexpected exception (a cancellation, an
            // OOM) could surface, and the checkpoint and connection dispose below must still run, or
            // a crash here leaks the connection on every shutdown that hits it.
            await _writerLoop.ConfigureAwait(false);
        }
        finally
        {
            Execute(_connection, "PRAGMA wal_checkpoint(FULL);");
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
