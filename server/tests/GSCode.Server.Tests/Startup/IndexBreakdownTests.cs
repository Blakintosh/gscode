using GSCode.Server.Startup;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Server.Tests.Startup;

/// <summary>
/// The startup log's index breakdown splits each language's files by where they came from. A devraw
/// file is raw context, so counted by context alone it disappeared into the raw figure, and nothing
/// in the log said whether devraw had been found or indexed at all. The roots are the subject here,
/// so they are spelled out rather than taken from <c>TestPaths</c>.
/// </summary>
public class IndexBreakdownTests
{
    private const string DevRawRoot = @"c:\devraw";

    private static ScriptDatabase Database()
    {
        ScriptDatabase database = new();
        database.Gsc.Upsert(TestRecords.At(@"c:\raw\scripts\a.gsc", "raw", @"scripts\a"));
        database.Gsc.Upsert(TestRecords.At(@"c:\raw\scripts\b.gsc", "raw", @"scripts\b"));
        database.Gsc.Upsert(TestRecords.At(@"c:\devraw\scripts\dev.gsc", "raw", @"scripts\dev"));
        database.Gsc.Upsert(TestRecords.At(@"c:\mods\m\scripts\m.gsc", "mod:m", @"scripts\m"));
        return database;
    }

    [Fact]
    public void ADevrawFile_IsCountedApartFromRaw()
    {
        string report = IndexReporting.IndexBreakdown(Database(), DevRawRoot);

        Assert.Contains("4 files  (2 raw · 1 devraw · 1 mod)", report, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoDevrawRoot_EveryRawContextFileIsRaw()
    {
        string report = IndexReporting.IndexBreakdown(Database(), devRawRoot: null);

        Assert.Contains("4 files  (3 raw · 1 mod)", report, StringComparison.Ordinal);
    }
}
