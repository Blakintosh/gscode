using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The class graph's own bookkeeping, tested directly rather than through a store, because what can
/// go wrong here is a stale bucket after a file changes — and that is invisible from a single query.
/// </summary>
public class ClassGraphTests
{
    private static readonly TextRange Anywhere = new(new Position(1, 1), new Position(1, 5));

    private static FunctionSymbol Method(string name)
    {
        return new FunctionSymbol
        {
            Name = name,
            KeyName = name.ToLowerInvariant(),
            Namespace = "",
            NameRange = Anywhere,
            FullRange = Anywhere,
        };
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
        return new ClassSymbol
        {
            Name = name,
            KeyName = name.ToLowerInvariant(),
            Namespace = "",
            ParentKeyName = parent?.ToLowerInvariant(),
            Methods = [.. methods.Select(Method)],
            NameRange = Anywhere,
            FullRange = Anywhere,
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
    public void Remove_DropsEveryClassTheFileContributed()
    {
        ClassGraph graph = new();
        graph.Apply(@"C:\raw\a.gsc", [Class("cScene", parent: "cBase", methods: "play")]);

        graph.Remove(@"C:\raw\a.gsc");

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

        graph.Remove(@"C:\raw\a.gsc");

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
            graph.Remove($@"C:\raw\{index}.gsc");
        });

        Assert.Empty(graph.PathsDeclaring("cshared"));
        Assert.Empty(graph.DirectChildren("cbase"));
        Assert.Empty(graph.ClassesDeclaringMethod("play"));
        Assert.Empty(graph.AllDeclaringPaths());
    }
}
