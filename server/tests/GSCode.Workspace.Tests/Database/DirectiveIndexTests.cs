using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The header watcher and the rename planner read the directive index instead of every record's
/// edges. These keep both old walks as reference implementations and require the same answer from
/// the indexed ones, across the spellings a directive can take, a header-to-header chain, a header
/// that resolves nowhere yet, and edits and removals that must leave the index behind them.
/// </summary>
public class DirectiveIndexTests
{
    private static readonly TextRange s_someRange = TextRange.FromCoordinates(0, 8, 0, 20);

    private static DependencyEdge Insert(string rawPath, string resolvedPath)
    {
        return new DependencyEdge(rawPath, resolvedPath, IsInsert: true, s_someRange);
    }

    private static DependencyEdge Using(string rawPath)
    {
        return new DependencyEdge(rawPath, "", IsInsert: false, s_someRange);
    }

    private static ScriptRecord Record(string path, string relativePath, params DependencyEdge[] edges)
    {
        ScriptLanguage language = path.EndsWith(".gsh", StringComparison.Ordinal)
            ? ScriptLanguage.Gsh
            : path.EndsWith(".csc", StringComparison.Ordinal) ? ScriptLanguage.Csc : ScriptLanguage.Gsc;

        return new ScriptRecord
        {
            Path = path,
            Language = language,
            ContextId = "raw",
            ContentHash = 0,
            RelativePath = relativePath,
            Dependencies = [.. edges],
        };
    }

    private const string Base = @"c:\raw\scripts\shared\base.gsh";
    private const string Wrapper = @"c:\raw\scripts\shared\wrapper.gsh";
    private const string Loop = @"c:\raw\scripts\shared\loop.gsh";

    /// <summary>
    /// base.gsh, inserted by wrapper.gsh, inserted by scripts in both languages; a header cycle; a
    /// script waiting on a header that has not resolved; and every spelling of a directive path the
    /// two comparisons fold together or keep apart.
    /// </summary>
    private static ScriptDatabase Workspace()
    {
        ScriptDatabase database = new();
        database.CommitRecord(Record(Base, @"scripts\shared\base.gsh"));
        database.CommitRecord(Record(Wrapper, @"scripts\shared\wrapper.gsh", Insert(@"scripts\shared\base.gsh", Base)));
        database.CommitRecord(Record(Loop, @"scripts\shared\loop.gsh",
            Insert(@"scripts\shared\wrapper.gsh", Wrapper), Insert(@"scripts\shared\loop.gsh", Loop)));

        database.CommitRecord(Record(@"c:\raw\scripts\a.gsc", @"scripts\a.gsc",
            Insert(@"scripts\shared\wrapper.gsh", Wrapper), Using(@"scripts\shared\util")));
        database.CommitRecord(Record(@"c:\raw\scripts\b.csc", @"scripts\b.csc",
            Insert(@"Scripts/Shared/Base.gsh", Base), Using(@"\Scripts\Shared\Util")));
        database.CommitRecord(Record(@"c:\raw\scripts\c.gsc", @"scripts\c.gsc",
            Insert(@"  scripts\shared\base.gsh ", ""), Using(@"/scripts/shared/util")));
        database.CommitRecord(Record(@"c:\raw\scripts\d.gsc", @"scripts\d.gsc",
            Insert(@"\scripts\shared\base.gsh", ""), Using(@"scripts\shared\util_other")));
        database.CommitRecord(Record(@"c:\raw\scripts\e.gsc", @"scripts\e.gsc",
            Insert(@"scripts\shared\loop.gsh", Loop), Using(@"scripts\shared\util.gsc")));
        database.CommitRecord(Record(@"c:\raw\scripts\f.gsc", @"scripts\f.gsc", Using(@"scripts\shared\wrapper.gsh")));
        return database;
    }

    /// <summary>The walk ScriptsInserting replaced: every header per round, then every script.</summary>
    private static List<string> ReferenceScriptsInserting(ScriptDatabase database, string header, string headerRelativePath)
    {
        HashSet<string> changed = new(StringComparer.Ordinal) { header };
        bool grew = true;
        while ( grew )
        {
            grew = false;
            foreach ( ScriptRecord record in database.AllGshRecords.ToList() )
            {
                if ( !changed.Contains(record.Path) && InsertsAny(record, changed, headerRelativePath) )
                {
                    changed.Add(record.Path);
                    grew = true;
                }
            }
        }

        List<string> inserting = [];
        foreach ( ScriptRecord record in database.Gsc.AllRecords.Concat(database.Csc.AllRecords) )
        {
            if ( InsertsAny(record, changed, headerRelativePath) )
            {
                inserting.Add(record.Path);
            }
        }

        inserting.Sort(StringComparer.Ordinal);
        return inserting;
    }

    private static bool InsertsAny(ScriptRecord record, HashSet<string> changed, string headerRelativePath)
    {
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert )
            {
                continue;
            }

            if ( changed.Contains(edge.ResolvedPath) )
            {
                return true;
            }

            if ( headerRelativePath.Length > 0
                && string.Equals(PathUtil.NormalizeScriptPath(edge.RawPath), headerRelativePath, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The walk PlanRename replaced: every record in every store.</summary>
    private static List<string> ReferencePlanRename(ScriptDatabase database, string oldPath, string newPath, bool isInsert)
    {
        string wanted = Canonical(oldPath);
        List<string> edits = [];
        if ( wanted == Canonical(newPath) )
        {
            return edits;
        }

        foreach ( ScriptRecord record in database.AllRecords )
        {
            foreach ( DependencyEdge edge in record.Dependencies )
            {
                if ( edge.IsInsert == isInsert && Canonical(edge.RawPath) == wanted )
                {
                    edits.Add(record.Path);
                }
            }
        }

        edits.Sort(StringComparer.Ordinal);
        return edits;
    }

    private static string Canonical(string directivePath)
    {
        return directivePath.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
    }

    private static List<string> Indexed(ScriptDatabase database, string header, string headerRelativePath)
    {
        List<string> paths = [];
        foreach ( ScriptRecord record in DatabaseQueries.ScriptsInserting(database, header, headerRelativePath) )
        {
            paths.Add(record.Path);
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static List<string> IndexedRename(ScriptDatabase database, string oldPath, string newPath, bool isInsert)
    {
        List<string> paths = [];
        foreach ( DependencyEdit edit in DependencyRewrite.PlanRename(database, oldPath, newPath, isInsert) )
        {
            paths.Add(edit.FilePath);
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    public static TheoryData<string, string> Headers()
    {
        return new TheoryData<string, string>
        {
            { Base, @"scripts\shared\base.gsh" },
            { Base, "" },
            { Wrapper, @"scripts\shared\wrapper.gsh" },
            { Loop, @"scripts\shared\loop.gsh" },
            { @"c:\raw\scripts\shared\new.gsh", @"scripts\shared\new.gsh" },
        };
    }

    [Theory]
    [MemberData(nameof(Headers))]
    public void ScriptsInserting_MatchesTheWalkItReplaced(string header, string headerRelativePath)
    {
        ScriptDatabase database = Workspace();

        Assert.Equal(ReferenceScriptsInserting(database, header, headerRelativePath), Indexed(database, header, headerRelativePath));
    }

    [Fact]
    public void ScriptsInserting_FollowsTheHeaderChainIntoBothLanguages()
    {
        ScriptDatabase database = Workspace();

        List<string> found = Indexed(database, Base, @"scripts\shared\base.gsh");

        // a.gsc through wrapper, e.gsc through loop -> wrapper, b.csc directly, c.gsc by written path
        // alone. d.gsc writes a leading backslash, which the watcher's comparison has never folded.
        Assert.Equal(
            [@"c:\raw\scripts\a.gsc", @"c:\raw\scripts\b.csc", @"c:\raw\scripts\c.gsc", @"c:\raw\scripts\e.gsc"],
            found);
    }

    public static TheoryData<string, bool> RenamedPaths()
    {
        return new TheoryData<string, bool>
        {
            { @"scripts\shared\util", false },
            { @"Scripts/Shared/Util", false },
            { @"scripts\shared\util_other", false },
            { @"scripts\shared\util.gsc", false },
            { @"scripts\shared\base.gsh", true },
            { @"scripts\shared\wrapper.gsh", true },
            { @"scripts\shared\wrapper.gsh", false },
            { @"scripts\shared\util", true },
            { @"scripts\shared\nothing", false },
        };
    }

    [Theory]
    [MemberData(nameof(RenamedPaths))]
    public void PlanRename_MatchesTheWalkItReplaced(string oldPath, bool isInsert)
    {
        ScriptDatabase database = Workspace();

        Assert.Equal(
            ReferencePlanRename(database, oldPath, @"scripts\renamed", isInsert),
            IndexedRename(database, oldPath, @"scripts\renamed", isInsert));
    }

    [Fact]
    public void AnEditThatDropsAnInsert_TakesTheFileOutOfBothAnswers()
    {
        ScriptDatabase database = Workspace();
        database.CommitRecord(Record(@"c:\raw\scripts\b.csc", @"scripts\b.csc", Using(@"scripts\shared\other")));
        database.CommitRecord(Record(Wrapper, @"scripts\shared\wrapper.gsh"));

        Assert.Equal(ReferenceScriptsInserting(database, Base, @"scripts\shared\base.gsh"), Indexed(database, Base, @"scripts\shared\base.gsh"));
        Assert.DoesNotContain(@"c:\raw\scripts\b.csc", Indexed(database, Base, @"scripts\shared\base.gsh"));
        Assert.DoesNotContain(@"c:\raw\scripts\a.gsc", Indexed(database, Base, @"scripts\shared\base.gsh"));
        Assert.Equal(
            ReferencePlanRename(database, @"scripts\shared\util", @"scripts\x", false),
            IndexedRename(database, @"scripts\shared\util", @"scripts\x", false));
    }

    [Fact]
    public void RemovedFiles_LeaveNeitherIndex()
    {
        ScriptDatabase database = Workspace();
        database.Remove(@"c:\raw\scripts\a.gsc", ScriptLanguage.Gsc);
        database.Remove(Wrapper, ScriptLanguage.Gsh);

        Assert.Equal(ReferenceScriptsInserting(database, Base, @"scripts\shared\base.gsh"), Indexed(database, Base, @"scripts\shared\base.gsh"));
        Assert.Empty(database.Gsc.FilesInserting(Wrapper));
        Assert.Empty(database.GshFilesInserting(Base));
        Assert.Equal(
            ReferencePlanRename(database, @"scripts\shared\wrapper.gsh", @"scripts\x.gsh", true),
            IndexedRename(database, @"scripts\shared\wrapper.gsh", @"scripts\x.gsh", true));

        // loop.gsh still writes the wrapper's path; only the removed files' edits are gone.
        Assert.Equal([Loop], IndexedRename(database, @"scripts\shared\wrapper.gsh", @"scripts\x.gsh", true));
    }

    [Theory]
    [InlineData(@"scripts\shared\util", @"scripts\shared\util")]
    [InlineData(@"Scripts/Shared/Util", @"scripts\shared\util")]
    [InlineData(@"\scripts\shared\util", @"scripts\shared\util")]
    [InlineData(@" \ \scripts\shared\util ", @"scripts\shared\util")]
    [InlineData(@"scripts\shared\base.gsh", @"scripts\shared\base.gsh")]
    public void WrittenKey_FoldsWhatBothComparisonsIgnore(string written, string expected)
    {
        Assert.Equal(expected, DirectiveIndex.WrittenKey(written));
        Assert.Equal(DirectiveIndex.WrittenKey(written), DirectiveIndex.WrittenKey(Canonical(written)));
        Assert.Equal(DirectiveIndex.WrittenKey(written), DirectiveIndex.WrittenKey(PathUtil.NormalizeScriptPath(written)));
    }
}
