using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// The reported bug: a file deleted (or renamed) between two sessions of the SAME persistent
/// cache leaves its row behind forever. <see cref="WatchedFileUpdater"/> prunes a row for a file
/// deleted WHILE the server is running, via <see cref="WorkspaceIndexer.RemoveFile"/> — but
/// nothing ever asked the mirror question for the ordinary case, a file removed while the server
/// was not running to see the watcher event at all.
/// </summary>
public class CacheRowPruningTests
{
    private static readonly string s_keptPath = TestPaths.Raw(@"scripts\kept.gsc");
    private static readonly string s_deletedPath = TestPaths.Raw(@"scripts\deleted.gsc");

    private static async Task<IReadOnlyDictionary<string, CachedEntry>> RunSessionAsync(
        FakeFileSystem files, SqliteCache cache)
    {
        RootConfig config = TestPaths.Config(files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());

        indexer.UseCache(cache, cache.LoadAll());
        await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);
        await cache.WaitForIdleAsync(CancellationToken.None);

        return cache.LoadAll();
    }

    [Fact]
    public async Task AFileDeletedBetweenSessions_HasItsCacheRowPruned()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"gscode-cache-prune-test-{Guid.NewGuid():N}.db");
        SqliteCache cache = SqliteCache.Open(dbPath, "test-identity");
        try
        {
            FakeFileSystem files = new FakeFileSystem()
                .AddFile(s_keptPath, "function kept()\n{\n}\n")
                .AddFile(s_deletedPath, "function deleted()\n{\n}\n");

            // Session 1: both files are analysed and cached.
            IReadOnlyDictionary<string, CachedEntry> afterFirstSession = await RunSessionAsync(files, cache);
            Assert.True(afterFirstSession.ContainsKey(PathUtil.NormalizeAbsolute(s_deletedPath)));

            // The file is deleted while the server is off — no watcher event, so RemoveFile's
            // own EnqueueDelete never runs for it.
            files.RemoveFile(s_deletedPath);

            // Session 2: deleted.gsc is no longer among EnumerateIndexTargets' results.
            IReadOnlyDictionary<string, CachedEntry> afterSecondSession = await RunSessionAsync(files, cache);

            Assert.False(afterSecondSession.ContainsKey(PathUtil.NormalizeAbsolute(s_deletedPath)));
            Assert.True(afterSecondSession.ContainsKey(PathUtil.NormalizeAbsolute(s_keptPath)));
        }
        finally
        {
            await cache.DisposeAsync();
            SqliteCache.DeleteDatabase(dbPath);
        }
    }
}
