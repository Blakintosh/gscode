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
    public async Task RoundTrip_PersistsAndRestoresRecords()
    {
        ScriptRecord record = SampleRecord(@"c:\ws\scripts\sample.gsc", 12345);

        await using ( SqliteCache cache = SqliteCache.Open(_dbPath, "identity-a") )
        {
            cache.Enqueue(record);
        }

        await using ( SqliteCache reopened = SqliteCache.Open(_dbPath, "identity-a") )
        {
            IReadOnlyDictionary<string, ScriptRecord> restored = reopened.LoadAll();

            ScriptRecord loaded = Assert.Single(restored).Value;
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
        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
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
            IReadOnlyDictionary<string, ScriptRecord> restored = cache2.LoadAll();
            Assert.Single(restored);
            indexer2.UseCache(cache2, restored);
            await indexer2.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        }

        Assert.Single(DatabaseQueries.LookupFunctions(db2.Gsc, "raw", "", null, "alpha"));
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
}
