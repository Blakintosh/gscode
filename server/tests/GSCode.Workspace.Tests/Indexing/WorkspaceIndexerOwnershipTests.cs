using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// F3: a startup (or folder-change) index pass must not overwrite an open document's committed
/// record with what disk currently says. A restored tab is `didOpen`ed during/around initialize —
/// before the index that races it finishes — and its buffer, already committed as
/// <c>isDirty: true</c> by the text-sync handler, is the source of truth until the next save.
/// <see cref="WatchedFileUpdater"/> already had this rule for on-disk changes; these tests are its
/// counterpart for the cold-start / folder-change index.
/// </summary>
public class WorkspaceIndexerOwnershipTests
{

    private static (ScriptDatabase Database, WorkspaceIndexer Indexer, PathResolver Resolver, string OpenPath)
        BuildWorkspaceWithOneOpenFile()
    {
        string openPath = TestPaths.Raw(@"scripts\open.gsc");

        // Disk disagrees with the open buffer on purpose: disk says `stale_disk_only`, the
        // buffer (committed below, as the text-sync handler would) says `edited_in_buffer`.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(openPath, "function stale_disk_only()\n{\n}\n");

        RootConfig config = TestPaths.Config(files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());

        ParseResult openBuffer = ScriptAnalysis.Analyze(
            openPath, ScriptLanguage.Gsc, SourceText.From("function edited_in_buffer()\n{\n}\n"),
            NullInsertProvider.Instance, new NameTable());
        database.Commit(openBuffer, resolver.GetContext(openPath), isDirty: true, "scripts\\open.gsc");

        return (database, indexer, resolver, openPath);
    }

    [Fact]
    public async Task IndexAsync_NeverOverwritesAnOpenDocumentsCommittedRecord()
    {
        (ScriptDatabase database, WorkspaceIndexer indexer, _, string openPath) = BuildWorkspaceWithOneOpenFile();

        await indexer.IndexAsync(
            IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None,
            ownedByEditor: candidate => string.Equals(candidate, PathUtil.NormalizeAbsolute(openPath), StringComparison.Ordinal));

        Assert.True(database.Gsc.TryGet(PathUtil.NormalizeAbsolute(openPath), out ScriptRecord record));
        Assert.True(record.IsDirty);
        Assert.Contains(record.Functions, function => function.Name == "edited_in_buffer");
        Assert.DoesNotContain(record.Functions, function => function.Name == "stale_disk_only");
    }

    [Fact]
    public async Task IndexAsync_WithNoOwnershipPredicate_OverwritesAsBefore()
    {
        // The regression this guards against the other way: an untouched ownedByEditor (the
        // default, for a caller with no open documents at all) must keep indexing everything, the
        // same as before this parameter existed.
        (ScriptDatabase database, WorkspaceIndexer indexer, _, string openPath) = BuildWorkspaceWithOneOpenFile();

        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

        Assert.True(database.Gsc.TryGet(PathUtil.NormalizeAbsolute(openPath), out ScriptRecord record));
        Assert.False(record.IsDirty);
        Assert.Contains(record.Functions, function => function.Name == "stale_disk_only");
    }

    [Fact]
    public async Task IndexAsync_StillCachesTheDiskContentForAnOwnedFile()
    {
        // The other half of the fix: NOT committing to the live store must not mean the disk
        // content is lost to the persistent cache either — a warm restart still wants what is
        // actually saved, since the open buffer's unsaved edits are exactly that: unsaved.
        (ScriptDatabase database, WorkspaceIndexer indexer, PathResolver resolver, string openPath) =
            BuildWorkspaceWithOneOpenFile();

        List<ScriptRecord> enqueued = [];
        GSCode.Workspace.Cache.SqliteCache? cache = null;
        try
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"gscode-ownership-test-{Guid.NewGuid():N}.db");
            cache = GSCode.Workspace.Cache.SqliteCache.Open(dbPath, "test-identity");
            indexer.UseCache(cache, new Dictionary<string, GSCode.Workspace.Cache.CachedEntry>());

            await indexer.IndexAsync(
                IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None,
                ownedByEditor: candidate => string.Equals(candidate, PathUtil.NormalizeAbsolute(openPath), StringComparison.Ordinal));

            await cache.WaitForIdleAsync(CancellationToken.None);

            IReadOnlyDictionary<string, GSCode.Workspace.Cache.CachedEntry> restored = cache.LoadAll();
            Assert.True(restored.TryGetValue(PathUtil.NormalizeAbsolute(openPath), out GSCode.Workspace.Cache.CachedEntry? entry));

            ScriptRecord? cached = entry!.Materialize();
            Assert.NotNull(cached);
            Assert.Contains(cached!.Functions, function => function.Name == "stale_disk_only");
        }
        finally
        {
            if ( cache is not null )
            {
                await cache.DisposeAsync();
            }
        }
    }
}
