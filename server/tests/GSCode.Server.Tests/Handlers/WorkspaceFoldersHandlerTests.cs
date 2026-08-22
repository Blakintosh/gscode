using System.Collections.Immutable;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

public class WorkspaceFoldersHandlerTests
{
    private static ScriptRecord RecordAt(string path, string contextId)
    {
        return new ScriptRecord
        {
            Path = PathUtil.NormalizeAbsolute(path),
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
        };
    }

    [Fact]
    public void AddedFolder_JoinsTheSet()
    {
        ImmutableArray<string> next = WorkspaceFoldersHandler.NextFolderSet(
            [@"C:\work\one"], [], [@"C:\work\two"]);

        Assert.Equal(2, next.Length);
        Assert.Contains(PathUtil.NormalizeAbsolute(@"C:\work\two"), next);
    }

    [Fact]
    public void RemovedFolder_LeavesTheSet()
    {
        ImmutableArray<string> next = WorkspaceFoldersHandler.NextFolderSet(
            [@"C:\work\one", @"C:\work\two"], [@"C:\work\two"], []);

        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\work\one"), Assert.Single(next));
    }

    [Fact]
    public void FolderNamedDifferently_StillMatches()
    {
        // The client may report a different casing or slash style than the stored form.
        ImmutableArray<string> next = WorkspaceFoldersHandler.NextFolderSet(
            [@"C:\work\one"], [@"c:/WORK/one"], []);

        Assert.Empty(next);
    }

    [Fact]
    public void FolderRemovedAndReAdded_Survives()
    {
        // Removals apply first, so a client that reports both keeps the folder.
        ImmutableArray<string> next = WorkspaceFoldersHandler.NextFolderSet(
            [@"C:\work\one"], [@"C:\work\one"], [@"C:\work\one"]);

        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\work\one"), Assert.Single(next));
    }

    [Fact]
    public void WorkspaceRecordUnderARemovedFolder_IsDropped()
    {
        ScriptRecord record = RecordAt(@"C:\work\two\scripts\a.gsc", @"workspace:c:\work\two");

        Assert.True(WorkspaceFoldersHandler.ShouldDropOnFolderRemoval(record, @"C:\work\two"));
    }

    [Fact]
    public void WorkspaceRecordUnderAnUnrelatedFolder_Survives()
    {
        ScriptRecord record = RecordAt(@"C:\work\one\scripts\a.gsc", @"workspace:c:\work\one");

        Assert.False(WorkspaceFoldersHandler.ShouldDropOnFolderRemoval(record, @"C:\work\two"));
    }

    [Fact]
    public void RawAndModRecords_SurviveEvenWhenPhysicallyUnderTheFolder()
    {
        // The important guard: raw and mod files stay reachable no matter which folders are
        // open, so dropping them would break resolution for every other file in the session.
        ScriptRecord raw = RecordAt(@"C:\work\two\share\raw\scripts\a.gsc", "raw");
        ScriptRecord mod = RecordAt(@"C:\work\two\mods\mod_a\scripts\a.gsc", "mod:mod_a");

        Assert.False(WorkspaceFoldersHandler.ShouldDropOnFolderRemoval(raw, @"C:\work\two"));
        Assert.False(WorkspaceFoldersHandler.ShouldDropOnFolderRemoval(mod, @"C:\work\two"));
    }
}
