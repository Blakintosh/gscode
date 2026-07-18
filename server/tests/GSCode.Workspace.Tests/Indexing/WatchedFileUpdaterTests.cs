using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

public class WatchedFileUpdaterTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static (ScriptDatabase Database, WatchedFileUpdater Updater, FakeFileSystem Files) Build()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define CAP 5\n")
            .AddFile(@$"{Raw}\scripts\uses_it.gsc", "#insert scripts\\shared\\shared.gsh;\nfunction f()\n{\nx = CAP;\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        return (database, new WatchedFileUpdater(database, indexer), files);
    }

    [Fact]
    public void ChangedGsc_ReindexesTheFile()
    {
        (ScriptDatabase database, WatchedFileUpdater updater, FakeFileSystem files) = Build();
        string path = @$"{Raw}\scripts\uses_it.gsc";

        files.AddFile(path, "function renamed()\n{\n}\n");
        IReadOnlyList<string> touched = updater.Apply(path, WatchedFileChange.Changed);

        Assert.Single(touched);
        Assert.Empty(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "f"));
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "renamed"));
    }

    [Fact]
    public void DeletedGsc_RemovesItFromTheStore()
    {
        (ScriptDatabase database, WatchedFileUpdater updater, _) = Build();
        string path = @$"{Raw}\scripts\uses_it.gsc";

        updater.Apply(path, WatchedFileChange.Deleted);

        Assert.Empty(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "f"));
        Assert.False(database.Gsc.TryGet(PathUtil.NormalizeAbsolute(path), out _));
    }

    [Fact]
    public void ChangedGsh_ReindexesEveryInsertingFile()
    {
        (ScriptDatabase database, WatchedFileUpdater updater, FakeFileSystem files) = Build();
        string gshPath = @$"{Raw}\scripts\shared\shared.gsh";

        // Change the macro; the dependent file must be re-indexed as a result.
        files.AddFile(gshPath, "#define CAP 99\n");
        IReadOnlyList<string> touched = updater.Apply(gshPath, WatchedFileChange.Changed);

        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\uses_it.gsc"), touched);
    }
}
