using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Cache;

public class SqliteCacheTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gscode_cache_test_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach ( string suffix in new[] { "", "-wal", "-shm" } )
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch ( IOException )
            {
            }
        }
    }

    private static ScriptRecord SampleRecord(string path, ulong hash)
    {
        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = "raw",
            RelativePath = @"scripts\sample.gsc",
            ContentHash = hash,
            Functions =
            [
                new FunctionSymbol
                {
                    Name = "Sample",
                    KeyName = "sample",
                    Namespace = "test",
                    NameRange = TextRange.FromCoordinates(0, 9, 0, 15),
                    FullRange = TextRange.FromCoordinates(0, 0, 2, 1),
                },
            ],
            References =
            [
                new ReferenceEntry(new SymbolKey("test", "sample", SymbolKind.Function), TextRange.FromCoordinates(0, 9, 0, 15), ReferenceKind.Definition),
            ],
        };
    }

    [Fact]
    public async Task NothingIsQueuedSilently_AndDropsAreCounted()
    {
        // Enqueue is deliberately non-blocking: it runs on the indexing threads and must not wait on
        // disk. But its result used to be discarded, and the channel is BOUNDED — TryWrite on a full
        // one returns false and the record is simply gone. BoundedChannelFullMode.Wait does not
        // apply to TryWrite, only to WriteAsync, so nothing about the configuration prevented it.
        //
        // The failure was invisible: the next start reported a normal warm restore and quietly
        // re-analysed whatever had been lost. This pins the counter that makes it observable, and the
        // ordinary case that must never report a drop.
        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "identity-a") )
        {
            for ( int index = 0; index < 64; index++ )
            {
                cache.Enqueue(SampleRecord(@$"c:\ws\scripts\sample{index}.gsc", (ulong)index));
            }

            Assert.Equal(0, cache.DroppedWrites);
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "identity-a") )
        {
            // Every record queued has to survive the round trip, which is the property the counter
            // exists to protect: a non-zero count means this assertion would have been wrong.
            Assert.Equal(64, reopened.LoadAll().Count);
        }
    }

    [Fact]
    public async Task RoundTrip_PersistsAndRestoresRecords()
    {
        ScriptRecord record = SampleRecord(@"c:\ws\scripts\sample.gsc", 12345);

        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "identity-a") )
        {
            cache.Enqueue(record);
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "identity-a") )
        {
            IReadOnlyDictionary<string, CachedEntry> restored = reopened.LoadAll();

            CachedEntry entry = Assert.Single(restored).Value;

            // The hash comes off its own COLUMN now, not out of the blob, and that is the whole
            // reason a warm start can decide a file is stale without deserializing it.
            Assert.Equal(record.ContentHash, entry.ContentHash);

            ScriptRecord? loaded = entry.Materialize();
            Assert.NotNull(loaded);
            Assert.Equal(record.Path, loaded.Path);
            Assert.Equal(record.ContentHash, loaded.ContentHash);
            Assert.Equal("Sample", Assert.Single(loaded.Functions).Name);
            Assert.Equal(record.References[0].Key, Assert.Single(loaded.References).Key);
        }
    }

    [Fact]
    public async Task IdentityMismatch_WipesTheCache()
    {
        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "identity-a") )
        {
            cache.Enqueue(SampleRecord(@"c:\ws\scripts\sample.gsc", 1));
        }

        // Reopen with a different server identity: the cache must be empty.
        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "identity-b") )
        {
            Assert.Empty(reopened.LoadAll());
        }
    }

    [Fact]
    public async Task DirtyRecords_AreNotPersisted()
    {
        ScriptRecord dirty = SampleRecord(@"c:\ws\scripts\sample.gsc", 1) with { IsDirty = true };

        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "id") )
        {
            cache.Enqueue(dirty);
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "id") )
        {
            Assert.Empty(reopened.LoadAll());
        }
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        ScriptRecord record = SampleRecord(@"c:\ws\scripts\sample.gsc", 1);

        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "id") )
        {
            cache.Enqueue(record);
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "id") )
        {
            reopened.EnqueueDelete(record.Path);
        }

        await using ( SqliteCache again = SqliteCache.Open(_dbPath, "id") )
        {
            Assert.Empty(again.LoadAll());
        }
    }

    [Fact]
    public async Task AMixedBatch_KeepsEachRecordsOwnValues()
    {
        // The upsert and delete statements are built once per BATCH and reused, with only their
        // parameter values reassigned per record. That makes leakage between records the failure
        // mode to guard: a value left over from the previous row, or a row skipped for being dirty
        // taking the next one's place. Every field here is distinct per record, so a leak shows up
        // as a wrong VALUE rather than merely a wrong count.
        //
        // The mix matters as much as the size. One batch carries clean records, a dirty one that
        // must be skipped, and a delete, which is the arrangement a real edit session produces.
        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "id") )
        {
            cache.Enqueue(SampleRecord(@"c:\ws\scripts\doomed.gsc", 900));

            for ( int index = 0; index < 8; index++ )
            {
                cache.Enqueue(SampleRecord(@$"c:\ws\scripts\keep{index}.gsc", (ulong)(100 + index)));
            }

            cache.Enqueue(SampleRecord(@"c:\ws\scripts\dirty.gsc", 777) with { IsDirty = true });
            cache.EnqueueDelete(@"c:\ws\scripts\doomed.gsc");
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "id") )
        {
            IReadOnlyDictionary<string, CachedEntry> restored = reopened.LoadAll();

            Assert.Equal(8, restored.Count);
            for ( int index = 0; index < 8; index++ )
            {
                CachedEntry entry = restored[PathUtil.NormalizeAbsolute(@$"c:\ws\scripts\keep{index}.gsc")];

                // Both sides of the row, because leakage between records is what this test is for:
                // the column the freshness check reads, and the blob behind it.
                Assert.Equal((ulong)(100 + index), entry.ContentHash);

                ScriptRecord? loaded = entry.Materialize();
                Assert.NotNull(loaded);
                Assert.Equal((ulong)(100 + index), loaded.ContentHash);
            }
        }
    }
}

/// <summary>Cold-restore behavior wired through the indexer against a fake file tree.</summary>
public class ColdRestoreTests : IDisposable
{
    private const string Raw = @"C:\bo3\share\raw";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "gscode_restore_test_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        foreach ( string suffix in new[] { "", "-wal", "-shm" } )
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch ( IOException )
            {
            }
        }
    }

    private static (ScriptDatabase Db, WorkspaceIndexer Indexer, FakeFileSystem Files) Build(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        return (database, indexer, files);
    }

    [Fact]
    public async Task SecondColdStart_RestoresUnchangedFilesFromCache()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\a.gsc", "function alpha()\n{\n}\n");

        // First cold start populates the cache.
        (ScriptDatabase db1, WorkspaceIndexer indexer1, _) = Build(files);
        await using ( SqliteCache cache1 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer1.UseCache(cache1, cache1.LoadAll());
            await indexer1.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        // Second cold start: the file is unchanged, so it restores from cache.
        (ScriptDatabase db2, WorkspaceIndexer indexer2, _) = Build(files);
        await using ( SqliteCache cache2 = SqliteCache.Open(_dbPath, "id") )
        {
            IReadOnlyDictionary<string, CachedEntry> restored = cache2.LoadAll();
            Assert.Single(restored);
            indexer2.UseCache(cache2, restored);
            await indexer2.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        Assert.Single(DatabaseQueries.LookupFunctions(db2.Gsc, "raw", "", null, "alpha"));
    }

    /// <summary>
    /// The restored/analysed split is what labels a run cold or warm in the memory report, and
    /// the two have very different allocation profiles, so the counts have to be truthful.
    /// </summary>
    [Fact]
    public async Task IndexOutcome_ReportsTheRestoredVersusAnalysedSplit()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\a.gsc", "function alpha()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\b.gsc", "function beta()\n{\n}\n");

        IndexOutcome cold;
        (ScriptDatabase _, WorkspaceIndexer indexer1, _) = Build(files);
        await using ( SqliteCache cache1 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer1.UseCache(cache1, cache1.LoadAll());
            cold = await indexer1.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        // Nothing cached yet, so every file goes through the full pipeline.
        Assert.Equal(2, cold.Total);
        Assert.Equal(0, cold.Restored);
        Assert.Equal(2, cold.Analysed);

        IndexOutcome warm;
        (ScriptDatabase _, WorkspaceIndexer indexer2, _) = Build(files);
        await using ( SqliteCache cache2 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer2.UseCache(cache2, cache2.LoadAll());
            warm = await indexer2.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        // Unchanged files come straight from the cache, skipping analysis entirely.
        Assert.Equal(2, warm.Total);
        Assert.Equal(2, warm.Restored);
        Assert.Equal(0, warm.Analysed);
    }

    [Fact]
    public async Task ChangedGshBetweenStarts_ReindexesDependents()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define CAP 5\n")
            .AddFile(@$"{Raw}\scripts\uses.gsc", "#insert scripts\\shared\\shared.gsh;\nfunction f()\n{\nx = CAP;\n}\n");

        (ScriptDatabase db1, WorkspaceIndexer indexer1, _) = Build(files);
        await using ( SqliteCache cache1 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer1.UseCache(cache1, cache1.LoadAll());
            await indexer1.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        // The header changes while the dependent's own text is byte-identical: phase two
        // must still re-parse the dependent so the macro edit propagates.
        files.AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define CAP 99\n");

        (ScriptDatabase db2, WorkspaceIndexer indexer2, _) = Build(files);
        await using ( SqliteCache cache2 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer2.UseCache(cache2, cache2.LoadAll());
            await indexer2.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        // The dependent resolves and the workspace is intact (no crash, no stale drop).
        Assert.Single(DatabaseQueries.LookupFunctions(db2.Gsc, "raw", "", null, "f"));
    }

    /// <summary>
    /// The chain case: base.gsh -> wrapper.gsh -> script.gsc, with only base.gsh changed between
    /// starts. wrapper.gsh's own bytes are untouched so it restores from cache, and marking only
    /// the headers that were ANALYSED left it out of the changed set — its dependents kept the
    /// record built against the OLD macro values for the rest of the session.
    ///
    /// The macro names the function, so what the second session extracts says which value it saw.
    /// </summary>
    [Fact]
    public async Task AChainedGshChangeBetweenStarts_ReachesTheFarDependent()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsh", "#define FN alpha\n")
            .AddFile(@$"{Raw}\scripts\wrapper.gsh", "#insert scripts\\base.gsh;\n")
            .AddFile(@$"{Raw}\scripts\script.gsc", "#insert scripts\\wrapper.gsh;\nfunction FN()\n{\n}\n");

        (ScriptDatabase db1, WorkspaceIndexer indexer1, _) = Build(files);
        await using ( SqliteCache cache1 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer1.UseCache(cache1, cache1.LoadAll());
            await indexer1.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        Assert.Single(DatabaseQueries.LookupFunctions(db1.Gsc, "raw", "", null, "alpha"));

        // Only the far end of the chain changes; both files between it and the script are
        // byte-identical and restore from the cache.
        files.AddFile(@$"{Raw}\scripts\base.gsh", "#define FN beta\n");

        (ScriptDatabase db2, WorkspaceIndexer indexer2, _) = Build(files);
        await using ( SqliteCache cache2 = SqliteCache.Open(_dbPath, "id") )
        {
            indexer2.UseCache(cache2, cache2.LoadAll());
            await indexer2.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        Assert.Single(DatabaseQueries.LookupFunctions(db2.Gsc, "raw", "", null, "beta"));
        Assert.Empty(DatabaseQueries.LookupFunctions(db2.Gsc, "raw", "", null, "alpha"));
    }
}
