using System.Collections.Immutable;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The relative-path index stands in for normalizing every record's path and comparing it against
/// an import list, so it has to find exactly the records that comparison found: both halves of an
/// overlay pair, case and slashes and extension ignored, and nothing left behind by an edit or a
/// removal.
/// </summary>
public class RelativePathIndexTests
{
    [Fact]
    public void AnOverlayAndTheRawFileItShadows_AreBothFoundAtTheirSharedPath()
    {
        LanguageStore store = new();
        store.Upsert(TestRecords.At(@"c:\raw\scripts\shared\util_shared.gsc", "raw", @"scripts\shared\util_shared.gsc"));
        store.Upsert(TestRecords.At(@"c:\mods\m\scripts\shared\util_shared.gsc", "mod:m", @"scripts/Shared/Util_Shared.gsc"));
        store.Upsert(TestRecords.At(@"c:\raw\scripts\shared\other.gsc", "raw", @"scripts\shared\other.gsc"));

        ImmutableArray<string> found = store.FilesAt(RelativePathIndex.Normalize(@"scripts\shared\util_shared"));

        Assert.Equal(2, found.Length);
        Assert.Contains(@"c:\raw\scripts\shared\util_shared.gsc", found);
        Assert.Contains(@"c:\mods\m\scripts\shared\util_shared.gsc", found);
    }

    [Fact]
    public void ARecordWhoseRelativePathChanges_MovesInTheIndex()
    {
        LanguageStore store = new();
        store.Upsert(TestRecords.At(@"c:\ws\a.gsc", "workspace:c:\\ws", @"scripts\a.gsc"));

        store.Upsert(TestRecords.At(@"c:\ws\a.gsc", "workspace:c:\\ws", @"scripts\b.gsc"));

        Assert.Empty(store.FilesAt(RelativePathIndex.Normalize(@"scripts\a")));
        Assert.Equal(@"c:\ws\a.gsc", Assert.Single(store.FilesAt(RelativePathIndex.Normalize(@"scripts\b"))));
    }

    [Fact]
    public void ARemovedRecord_AndOneOutsideEveryRoot_AreNotFound()
    {
        LanguageStore store = new();
        store.Upsert(TestRecords.At(@"c:\raw\scripts\a.gsc", "raw", @"scripts\a.gsc"));
        store.Upsert(TestRecords.At(@"c:\elsewhere\b.gsc", "workspace:c:\\elsewhere", ""));

        store.Remove(@"c:\raw\scripts\a.gsc");

        Assert.Empty(store.FilesAt(RelativePathIndex.Normalize(@"scripts\a")));
        Assert.Empty(store.FilesAt(""));
    }

    [Fact]
    public void RecordsAt_AsksEachPathOnce()
    {
        LanguageStore store = new();
        store.Upsert(TestRecords.At(@"c:\raw\scripts\a.gsc", "raw", @"scripts\a.gsc"));

        string path = RelativePathIndex.Normalize(@"scripts\a");
        List<ScriptRecord> records = DatabaseQueries.RecordsAt(store, [path, path]);

        Assert.Single(records);
    }
}
