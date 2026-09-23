using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using Microsoft.Data.Sqlite;

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
        // Unbounded, deliberately — see Enqueue for why a bound here meant losing writes. NOT marked
        // SingleReader even though there is one: that was once required, because WaitForIdleAsync
        // read this channel's Count and the single-reader unbounded channel cannot report it. It no
        // longer reads Count — idleness is one counter of outstanding commands now — so the option
        // is merely unused rather than unavailable. Turning it on is a throughput change with no
        // measurement behind it, which is its own commit.
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
            Execute(connection, "DELETE FROM files; DELETE FROM deps; DELETE FROM meta;");
            WriteMeta(connection, serverBuildIdentity);
        }

        return new SqliteCache(connection);
    }

    /// <summary>
    /// Reads every cached entry (warm-restore input) WITHOUT deserializing any of them.
    ///
    /// This used to return finished records, which made it the whole cost of a warm start: one
    /// thread inflating and reading records over every file's references and diagnostics, run to
    /// completion before <c>IndexAsync</c> was called at all. Measured back to back in one process,
    /// uninstrumented, that restore against a cold index of the same tree: bo3 1,509 ms against
    /// 390 ms, cod4 720 against 236, bo1 2,747 against 718. The analysis the cache exists to avoid
    /// runs at <c>ProcessorCount - 1</c>, so a serial restore made the cache four times slower than
    /// the work it saved. Nothing had ever measured it, because the server calls this as an
    /// ARGUMENT to <c>UseCache</c> — outside the stopwatch that times indexing.
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
            if ( !ulong.TryParse(reader.GetString(1), out ulong contentHash) )
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
    /// The channel used to be bounded at 4,096 and fed with <c>TryWrite</c>, and the writer did the
    /// serializing — one record at a time on one thread — while every indexing thread
    /// produced records. On a stock corpus the backlog never reached the bound. At 50,000 files it
    /// refused 40,889 of them (see PERF.md's scale section), and the next start re-analysed four
    /// files in five while reporting itself warm.
    ///
    /// Two changes, each needed. The serializing now happens HERE, on the enqueuing thread, so it
    /// runs across every indexing core instead of one and the writer is left with the SQL alone —
    /// which is what makes the backlog small. And the channel is unbounded, so a backlog that does
    /// build (a slow disk, an antivirus scan of the database) costs memory rather than data. What it
    /// can hold is bounded anyway: the compressed blobs of the workspace, about 7 KB a file, and only
    /// until the writer reaches them.
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
    /// instant in which a command is readable but uncounted — which is exactly the instant the old
    /// idle test could observe. If the channel refuses the write (only possible once
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
    /// a single writer, so the writer can still be going after IndexAsync returns — and compacting the heap while it works measures a moment that is about to
    /// be undone, which is exactly the "memory drops then climbs again" the server used to report.
    ///
    /// Polling rather than a signal, deliberately: this is called once per index by one caller that
    /// is already waiting, so a signal would buy nothing it can use.
    ///
    /// What it polls is ONE counter of outstanding commands, incremented before a command reaches
    /// the channel and decremented only after its transaction has committed. It used to be the
    /// channel's own Count plus a flag the writer raised, and that pair had a hole in it: the writer
    /// takes a command OFF the channel — dropping Count to zero — and only then raises the flag, so
    /// for that instant the queue looked empty and nothing looked busy. A poll landing there
    /// returned "idle" with a write still in flight, and the caller read a database one commit
    /// behind. `CacheRowPruningTests` caught it at roughly one run in seven, and only when other
    /// tests were loading the thread pool enough to widen the gap.
    ///
    /// The counter is mutated by every producer, which the previous comment here rejected on hot-path
    /// grounds. That cost is one `Interlocked.Increment` beside the channel's own bookkeeping in
    /// `TryWrite`, which is already interlocked — the same order of cost, on a path that then
    /// serializes and compresses a record.
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
                // re-analysed next cold start. Not just SqliteException: a bug in
                // RecordSerializer.Serialize, or anything else ApplyBatch can throw, used to fault
                // this whole loop permanently — the `await foreach` exits, nothing drains the
                // channel again for the rest of the session, and every later write silently piles
                // up as a dropped write. One bad batch degraded the entire run's cache.
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
    /// parameter collection and seven boxed values every time, for every file of a cold index. That
    /// is the same cost the <c>deps</c> write was removed for, noted below; it applied per edge
    /// there and per file here.
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
        command.CommandText = "DELETE FROM files WHERE path = $path; DELETE FROM deps WHERE path = $path;";
        command.Parameters.AddWithValue("$path", "");
        return command;
    }

    private static void ApplyUpsert(UpsertCommand command, SqliteCommand upsert)
    {
        upsert.Parameters["$path"].Value = command.Path;
        upsert.Parameters["$language"].Value = command.Language;
        upsert.Parameters["$context"].Value = command.ContextId;
        upsert.Parameters["$relative"].Value = command.RelativePath;
        upsert.Parameters["$hash"].Value = command.ContentHash.ToString();
        upsert.Parameters["$at"].Value = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        upsert.Parameters["$record"].Value = command.Blob;
        upsert.ExecuteNonQuery();

        // The `deps` table is deliberately NOT written. Nothing reads it: the same dependency edges
        // travel inside the serialized record (ScriptRecord.Dependencies), and that is what the
        // indexer's phase two uses to find files whose headers changed. Writing it cost a DELETE
        // plus one freshly-built SqliteCommand per edge per file — new command object, new parameter
        // collection, SQL re-parsed each time — plus maintaining ix_deps_dep, for rows no query ever
        // selected. The table stays in the schema so an existing database still opens.
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

        try
        {
            // The writer loop no longer faults on an ordinary write failure, but awaiting it is
            // still the one place a truly unexpected exception (a cancellation, an OOM) could
            // surface — and even then the checkpoint and the connection dispose below must still
            // run, or a crash here leaks the connection on every shutdown that hits it.
            await _writerLoop.ConfigureAwait(false);
        }
        finally
        {
            Execute(_connection, "PRAGMA wal_checkpoint(FULL);");
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
