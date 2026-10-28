using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The class graph's own bookkeeping, tested directly rather than through a store, because what can
/// go wrong here is a stale bucket after a file changes — and that is invisible from a single query.
/// </summary>
public class ClassGraphTests
{

    private static FunctionSymbol Method(string name)
    {
        return TestRecords.Function(name.ToLowerInvariant()) with { Name = name };
    }

    /// <summary>
    /// Order-insensitive set comparison. Asserting an ImmutableArray against a collection expression
    /// silently binds xUnit's single-value overload, and ImmutableArray equality is reference
    /// equality on the backing array — so that form fails even when the contents match.
    /// </summary>
    private static void AssertNames(ImmutableArray<string> actual, params string[] expected)
    {
        Assert.Equal(expected.Order().ToArray(), actual.Order().ToArray());
    }

    private static ClassSymbol Class(string name, string? parent = null, params string[] methods)
    {
        return TestRecords.Class(name) with
        {
            ParentKeyName = parent?.ToLowerInvariant(),
            Methods = [.. methods.Select(Method)],
        };
    }

    [Fact]
    public void Apply_IndexesEveryClassInTheFile()
    {
        ClassGraph graph = new();

        graph.Apply(@"C:\raw\a.gsc", [Class("cScene"), Class("cSceneObject")]);

        AssertNames(graph.PathsDeclaring("cscene"), @"C:\raw\a.gsc");
        AssertNames(graph.PathsDeclaring("csceneobject"), @"C:\raw\a.gsc");
    }

    [Fact]
    public void Apply_ReplacesThePreviousContributionOfThatFile()
    {
        // The failure this guards is a rename leaving the old name resolvable forever: the graph is
        // rebuilt per file on every keystroke that reaches indexing, so a leaked bucket never heals.
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cOld", parent: "cBase", methods: "play")]);

        graph.Apply(@"C:\raw\a.gsc", [Class("cNew", parent: "cOther", methods: "stop")]);

        Assert.Empty(graph.PathsDeclaring("cold"));
        Assert.Empty(graph.DirectChildren("cbase"));
        Assert.Empty(graph.ClassesDeclaringMethod("play"));
        AssertNames(graph.PathsDeclaring("cnew"), @"C:\raw\a.gsc");
        AssertNames(graph.DirectChildren("cother"), "cnew");
        AssertNames(graph.ClassesDeclaringMethod("stop"), "cnew");
    }

    [Fact]
    public void ApplyingNoClasses_DropsEveryClassTheFileContributed()
    {
        // An empty contribution IS the removal — see ClassGraph.Apply.
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", parent: "cBase", methods: "play")]);

        graph.Apply(@"C:\raw\a.gsc", []);

        Assert.Empty(graph.PathsDeclaring("cscene"));
        Assert.Empty(graph.DirectChildren("cbase"));
        Assert.Empty(graph.ClassesDeclaringMethod("play"));
        Assert.Empty(graph.AllClassNames());
        Assert.Empty(graph.AllDeclaringPaths());
    }

    [Fact]
    public void RemovingOneOfTwoFilesDeclaringTheSameName_LeavesTheOtherResolvable()
    {
        // The reverse maps are path-valued precisely so this works: a mod overlay and the raw script
        // it shadows both declare cScene, and dropping one must not evict the other's bucket.
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", parent: "cBase", methods: "play")]);
        graph.Apply(@"C:\mods\m\a.gsc", [Class("cScene", parent: "cBase", methods: "play")]);

        graph.Apply(@"C:\raw\a.gsc", []);

        AssertNames(graph.PathsDeclaring("cscene"), @"C:\mods\m\a.gsc");
        AssertNames(graph.DirectChildren("cbase"), "cscene");
        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene");
    }

    [Fact]
    public void DirectChildren_ExcludesGrandchildren()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene"), Class("cAwarenessScene", parent: "cScene")]);
        graph.Apply(@"C:\raw\b.gsc", [Class("cDeeper", parent: "cAwarenessScene")]);

        AssertNames(graph.DirectChildren("cscene"), "cawarenessscene");
    }

    [Fact]
    public void DirectChildren_DeduplicatesAcrossFilesDeclaringTheSameChild()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cChild", parent: "cBase")]);
        graph.Apply(@"C:\mods\m\a.gsc", [Class("cChild", parent: "cBase")]);

        AssertNames(graph.DirectChildren("cbase"), "cchild");
    }

    [Fact]
    public void ClassesDeclaringMethod_ReturnsEveryDeclarer()
    {
        // This is what resolves an arrow call whose receiver has no known class, so both answers
        // matter: one declarer means navigable, several means offer the candidates.
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", null, "play", "stop")]);
        graph.Apply(@"C:\raw\b.gsc", [Class("cSceneObject", methods: "play")]);

        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene", "csceneobject");
        AssertNames(graph.ClassesDeclaringMethod("stop"), "cscene");
    }

    [Fact]
    public void ClassesDeclaringMethod_IsEmptyForAnUnknownName()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", methods: "play")]);

        Assert.Empty(graph.ClassesDeclaringMethod("nosuchmethod"));
    }

    [Fact]
    public void AllDeclaringPaths_ExcludesAFileWhoseClassesWereAllRemoved()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene")]);

        graph.Apply(@"C:\raw\a.gsc", []);

        Assert.Empty(graph.AllDeclaringPaths());
    }

    [Fact]
    public void ConcurrentApplies_LeaveEveryFileExactlyOnce()
    {
        // Indexing runs Parallel.ForEachAsync, so Apply is genuinely concurrent.
        ClassGraph graph = new();

        Parallel.For(0, 200, index =>
        {
            graph.Apply($@"C:\raw\{index}.gsc", [Class("cShared", methods: "play")]);
        });

        Assert.Equal(200, graph.PathsDeclaring("cshared").Length);
        AssertNames(graph.ClassesDeclaringMethod("play"), "cshared");
    }

    [Fact]
    public void ConcurrentApplyAndRemoveOfOneFile_LeavesNoOrphanedBucket()
    {
        ClassGraph graph = new();

        Parallel.For(0, 200, index =>
        {
            graph.Apply($@"C:\raw\{index}.gsc", [Class("cShared", parent: "cBase", methods: "play")]);
            graph.Apply($@"C:\raw\{index}.gsc", []);
        });

        Assert.Empty(graph.PathsDeclaring("cshared"));
        Assert.Empty(graph.DirectChildren("cbase"));
        Assert.Empty(graph.ClassesDeclaringMethod("play"));
        Assert.Empty(graph.AllDeclaringPaths());
    }

    // Reads are cached on the graph and cleared by the write that changes them. The facts below ask
    // once BEFORE the write, so the cache is filled, and once after: a stale answer is what would
    // show if a write forgot to clear it.

    [Fact]
    public void AnAnswerAskedBeforeAWrite_IsNotWhatIsAnsweredAfterIt()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", null, "play")]);

        AssertNames(graph.PathsDeclaring("cscene"), @"C:\raw\a.gsc");
        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene");
        Assert.Contains("cscene", graph.ClassNameSet());

        graph.Apply(@"C:\raw\b.gsc", [Class("cScene", null, "stop"), Class("cOther", null, "play")]);

        AssertNames(graph.PathsDeclaring("cscene"), @"C:\raw\a.gsc", @"C:\raw\b.gsc");
        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene", "cother");
        Assert.Contains("cother", graph.ClassNameSet());

        graph.Apply(@"C:\raw\a.gsc", []);

        AssertNames(graph.PathsDeclaring("cscene"), @"C:\raw\b.gsc");
        AssertNames(graph.ClassesDeclaringMethod("play"), "cother");
        Assert.Contains("cscene", graph.ClassNameSet());
    }

    [Fact]
    public void AnEditThatKeepsTheFileInABucket_StillRefreshesTheNamesDerivedFromIt()
    {
        // The file stays in the "play" bucket throughout, under a different class: the derived
        // names must follow even though the bucket's path set ends where it began.
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", null, "play")]);
        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene");

        graph.Apply(@"C:\raw\a.gsc", [Class("cRenamed", null, "play")]);

        AssertNames(graph.ClassesDeclaringMethod("play"), "crenamed");
        Assert.DoesNotContain("cscene", graph.ClassNameSet());
    }

    [Fact]
    public void TheLastDeclarerRemoved_TakesTheNameOutOfTheSet()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene")]);
        Assert.Contains("cscene", graph.ClassNameSet());

        graph.Apply(@"C:\raw\a.gsc", []);

        Assert.DoesNotContain("cscene", graph.ClassNameSet());
        Assert.Empty(graph.PathsDeclaring("cscene"));
        Assert.Empty(graph.AllClassNames());
    }

    [Fact]
    public async Task ReadsRacingWrites_NeverThrow_AndSettleOnTheFinalAnswer()
    {
        // Readers take no lock, so this is the test of that: readers on every core while a writer
        // adds and removes files, then the answers once it stops.
        ClassGraph graph = new();
        using CancellationTokenSource stop = new();
        List<Task> readers = [];
        for ( int reader = 0; reader < Math.Max(2, Environment.ProcessorCount - 1); reader++ )
        {
            readers.Add(Task.Run(() =>
            {
                while ( !stop.IsCancellationRequested )
                {
                    _ = graph.PathsDeclaring("cscene");
                    _ = graph.ClassesDeclaringMethod("play");
                    _ = graph.DirectChildren("cbase");
                    _ = graph.ClassNameSet().Contains("cscene");
                }
            }));
        }

        for ( int round = 0; round < 2000; round++ )
        {
            string path = $@"C:\raw\f{round % 50}.gsc";
            graph.Apply(path, round % 3 == 0 ? [] : [Class("cScene", "cBase", "play")]);
        }

        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));

        for ( int file = 0; file < 50; file++ )
        {
            graph.Apply($@"C:\raw\f{file}.gsc", file < 10 ? [Class("cScene", "cBase", "play")] : []);
        }

        Assert.Equal(10, graph.PathsDeclaring("cscene").Length);
        AssertNames(graph.ClassesDeclaringMethod("play"), "cscene");
        AssertNames(graph.DirectChildren("cbase"), "cscene");
        Assert.True(graph.ClassNameSet().SetEquals(["cscene"]));
    }
}
