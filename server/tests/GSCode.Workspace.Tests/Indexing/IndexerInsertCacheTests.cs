using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// The indexer and the document path share ONE header cache.
///
/// They did not. The indexer kept a private <c>ConcurrentDictionary&lt;path,
/// Lazy&lt;InsertedFile?&gt;&gt;</c> while the same constructor was already handed the
/// <see cref="InsertCache"/> that the editor's providers use, so a workspace held BO3's 114 headers
/// twice over and each side warmed the other's copy not at all. Every assertion below reads false
/// against that shape.
/// </summary>
public class IndexerInsertCacheTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static (WorkspaceIndexer Indexer, InsertCache Inserts) Build(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        InsertCache inserts = new();
        return (new WorkspaceIndexer(database, () => resolver, files, new NameTable(), inserts), inserts);
    }

    private static FakeFileSystem Workspace()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define CAP 5\n")
            .AddFile(@$"{Raw}\scripts\uses_it.gsc", "#insert scripts\\shared\\shared.gsh;\nfunction f()\n{\nx = CAP;\n}\n");
    }

    [Fact]
    public async Task Indexing_FillsTheCacheTheDocumentPathReads()
    {
        FakeFileSystem files = Workspace();
        (WorkspaceIndexer indexer, InsertCache inserts) = Build(files);

        Assert.Equal(0, inserts.Count);

        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

        // One entry, not two, and in the cache the caller handed in rather than one the indexer
        // kept to itself. A file opened after this restores the header from here instead of
        // reading and lexing it again.
        Assert.Equal(1, inserts.Count);
    }

    [Fact]
    public async Task InvalidateGsh_ReachesTheSharedCache()
    {
        FakeFileSystem files = Workspace();
        (WorkspaceIndexer indexer, InsertCache inserts) = Build(files);

        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        Assert.Equal(1, inserts.Count);

        // The watcher's fast path when a header changes on disk. It used to drop the indexer's
        // private copy only, leaving the shared one to the timestamp check one call later.
        indexer.InvalidateGsh(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh"));

        Assert.Equal(0, inserts.Count);
    }

    [Fact]
    public async Task RemoveFile_DropsADeletedHeaderFromTheSharedCache()
    {
        FakeFileSystem files = Workspace();
        (WorkspaceIndexer indexer, InsertCache inserts) = Build(files);

        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        Assert.Equal(1, inserts.Count);

        indexer.RemoveFile(
            PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh"), ScriptLanguage.Gsh);

        Assert.Equal(0, inserts.Count);
    }
}
