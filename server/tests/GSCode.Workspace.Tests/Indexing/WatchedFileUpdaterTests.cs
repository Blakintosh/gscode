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

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
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

    // --- A file the editor has open ---
    //
    // The buffer is the source of truth there, and the text-sync handler already analyses it on
    // open, change and save. Reading disk behind that buffer is either the same file parsed twice
    // or, with unsaved edits, the editor's record replaced by older content — so every other file's
    // resolution describes text the user is not looking at until the next keystroke puts it back.

    [Fact]
    public void AnOpenFileIsNotReindexedFromDisk()
    {
        (ScriptDatabase database, WatchedFileUpdater updater, FakeFileSystem files) = Build();
        string path = @$"{Raw}\scripts\uses_it.gsc";

        // Disk moves on while the editor holds a different buffer.
        files.AddFile(path, "function renamed()\n{\n}\n");
        IReadOnlyList<string> touched = updater.Apply(path, WatchedFileChange.Changed, _ => true);

        Assert.Empty(touched);

        // Still the record the editor committed, not the one on disk.
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "f"));
        Assert.Empty(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "renamed"));
    }

    [Fact]
    public void AnOpenFileDeletedOnDiskKeepsItsRecord()
    {
        // The buffer outlives the file. Dropping the record would break every lookup into a
        // document the user can still see and save back; closing it is what retires the record.
        (ScriptDatabase database, WatchedFileUpdater updater, _) = Build();
        string path = @$"{Raw}\scripts\uses_it.gsc";

        updater.Apply(path, WatchedFileChange.Deleted, _ => true);

        Assert.True(database.Gsc.TryGet(PathUtil.NormalizeAbsolute(path), out _));
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "f"));
    }

    [Fact]
    public void AnOpenHeaderStillInvalidatesAndReindexesItsDependents()
    {
        // The exception that makes the skip safe. A header's side effects are facts about OTHER
        // files, and those are not open just because the header is — so skipping them would leave
        // every inserting file compiled against a header the editor has already replaced.
        (_, WatchedFileUpdater updater, FakeFileSystem files) = Build();
        string gshPath = @$"{Raw}\scripts\shared\shared.gsh";

        files.AddFile(gshPath, "#define CAP 9\n");
        IReadOnlyList<string> touched = updater.Apply(
            gshPath, WatchedFileChange.Changed, path => PathUtil.NormalizeAbsolute(path) == PathUtil.NormalizeAbsolute(gshPath));

        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\uses_it.gsc"), touched);
    }

    [Fact]
    public void AnOpenDependentOfAChangedHeaderIsNotReindexedFromDisk()
    {
        // The other half of the skip: a header's dependents are re-indexed FROM DISK, and one of
        // them being open makes that the same clobber the changed file's own gate prevents.
        (ScriptDatabase database, WatchedFileUpdater updater, FakeFileSystem files) = Build();
        string gshPath = @$"{Raw}\scripts\shared\shared.gsh";
        string dependent = PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\uses_it.gsc");

        // Disk has moved on behind the buffer whose record the database holds.
        files.AddFile(dependent, "function renamed()\n{\n}\n");
        files.AddFile(gshPath, "#define CAP 99\n");

        IReadOnlyList<string> touched = updater.Apply(
            gshPath, WatchedFileChange.Changed, path => PathUtil.NormalizeAbsolute(path) == dependent);

        // The header itself is not open, so it re-indexes; its open dependent does not.
        Assert.DoesNotContain(dependent, touched);
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "f"));
        Assert.Empty(DatabaseQueries.LookupFunctions(database.Gsc, "raw", "", null, "renamed"));
    }
}
