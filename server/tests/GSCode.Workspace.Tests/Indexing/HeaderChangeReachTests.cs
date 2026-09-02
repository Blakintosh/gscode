using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Indexing;

/// <summary>
/// Which files a header's change has to reach when it arrives from the WATCHER — a header edited
/// outside the editor, by another tool, or by a branch switch.
///
/// The startup index already closes the changed set over the header insert graph, and its comment
/// names the exact chain that forced it: base.gsh -> wrapper.gsh -> script.gsc, where the script
/// keeps a record built against the OLD macro values because the change reaches one hop only. The
/// watch path answers the same question with <see cref="ScriptDatabase.FilesInserting"/>, which
/// walks the GSC and CSC stores — headers live in a store of their own — so it reaches direct
/// non-header inserters and stops.
///
/// Observed through a macro that names a function, since a record carries the functions a file
/// declares but not the macros a header handed it.
/// </summary>
public class HeaderChangeReachTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private const string BasePath = @$"{Raw}\scripts\shared\base.gsh";
    private const string WrapperPath = @$"{Raw}\scripts\shared\wrapper.gsh";
    private const string ScriptPath = @$"{Raw}\scripts\uses_it.gsc";

    private static (ScriptDatabase Database, WatchedFileUpdater Updater, InsertCache Inserts) Build(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        InsertCache inserts = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable(), inserts);
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return (database, new WatchedFileUpdater(database, indexer), inserts);
    }

    private static bool Declares(ScriptDatabase database, string functionName)
    {
        return DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, functionName).Any();
    }

    [Fact]
    public void AChangeToANestedHeaderReachesTheScript()
    {
        // base.gsh is inserted by wrapper.gsh, which is inserted by the script. Only the script
        // declares anything, and what it declares is decided by base.gsh.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(BasePath, "#define FN_NAME first\n")
            .AddFile(WrapperPath, "#insert scripts\\shared\\base.gsh;\n")
            .AddFile(ScriptPath, "#insert scripts\\shared\\wrapper.gsh;\nfunction FN_NAME()\n{\n}\n");

        (ScriptDatabase database, WatchedFileUpdater updater, _) = Build(files);
        Assert.True(Declares(database, "first"));

        files.AddFile(BasePath, "#define FN_NAME second\n");
        updater.Apply(BasePath, WatchedFileChange.Changed);

        Assert.True(Declares(database, "second"));
        Assert.False(Declares(database, "first"));
    }

    [Fact]
    public void AHeaderAppearingReachesTheScriptThatWasWaitingForIt()
    {
        // The insert does not resolve yet, so the macro never arrives and the name stays as
        // written. The file that inserts it records no resolved path — which is why asking who
        // inserts the new header by its resolved path cannot find it.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(ScriptPath, "#insert scripts\\shared\\base.gsh;\nfunction FN_NAME()\n{\n}\n");

        (ScriptDatabase database, WatchedFileUpdater updater, _) = Build(files);
        Assert.True(Declares(database, "fn_name"));

        files.AddFile(BasePath, "#define FN_NAME arrived\n");
        updater.Apply(BasePath, WatchedFileChange.Created);

        Assert.True(Declares(database, "arrived"));
    }

    [Fact]
    public void AHeaderDeletedWhileOpenStillLeavesTheCache()
    {
        // Dropping the lexed copy rode on removing the RECORD, and the record is deliberately kept
        // for a file the editor still has open. So deleting a header from a tab left every file
        // inserting it expanding a header that no longer exists, for the rest of the session.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(BasePath, "#define FN_NAME first\n")
            .AddFile(ScriptPath, "#insert scripts\\shared\\base.gsh;\nfunction FN_NAME()\n{\n}\n");

        (_, WatchedFileUpdater updater, InsertCache inserts) = Build(files);
        Assert.Equal(1, inserts.Count);

        files.RemoveFile(BasePath);
        updater.Apply(
            BasePath,
            WatchedFileChange.Deleted,
            path => PathUtil.NormalizeAbsolute(path) == PathUtil.NormalizeAbsolute(BasePath));

        Assert.Equal(0, inserts.Count);
    }
}
