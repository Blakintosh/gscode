using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Completing class methods.
///
/// None of this existed: the engine knew class NAMES only, so typing <c>cScene::</c> returned
/// nothing at all despite the class declaring 59 methods, <c>-&gt;</c> was not a context, and a bare
/// prefix inside a class body offered every namespace function and builtin but none of the methods
/// the name would actually reach.
/// </summary>
public class ClassMethodCompletionTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static CompletionEngine BuildWorld(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new CompletionEngine(database, BuiltinApiSet.Load(ApiDirectory), ObjectFields.Load(ApiDirectory));
    }

    private static ParseResult Analyze(string path, string text)
    {
        return ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(text),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable());
    }

    private static bool HasLabel(ImmutableArray<CompletionEntry> entries, string label)
    {
        return entries.Any(e => string.Equals(e.Label, label, StringComparison.Ordinal)
            || (e.Kind == CompletionKind.Function && e.Label.StartsWith(label + "(", StringComparison.Ordinal)));
    }

    [Fact]
    public void ClassQualified_OffersTheClassesMethods()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\scene.gsc", "class cScene\n{\n    function play()\n    {\n    }\n    function stop()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#namespace game;\nfunction run()\n{\n    cScene::\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 12));

        Assert.True(HasLabel(entries, "play"));
        Assert.True(HasLabel(entries, "stop"));
    }

    [Fact]
    public void ClassQualified_OffersInheritedMethods()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\scene.gsc",
                "class cScene\n{\n    function play()\n    {\n    }\n}\nclass cAwarenessScene : cScene\n{\n    function alert()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#namespace game;\nfunction run()\n{\n    cAwarenessScene::\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 21));

        Assert.True(HasLabel(entries, "alert"));
        Assert.True(HasLabel(entries, "play"));
    }

    [Fact]
    public void ANameThatIsBothANamespaceAndAClass_OffersBoth()
    {
        // phalanx.gsc's shape. Both forms are legal after the qualifier, so choosing one would hide
        // whichever the user meant.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\phalanx.gsc",
                "#namespace Phalanx;\nfunction _PruneDead( t )\n{\n}\nclass Phalanx\n{\n    function _Update()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#using scripts\\phalanx;\n#namespace game;\nfunction run()\n{\n    Phalanx::\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 13));

        Assert.True(HasLabel(entries, "_PruneDead"));
        Assert.True(HasLabel(entries, "_Update"));
    }

    [Fact]
    public void SelfArrowInsideAClass_OffersThatClassesChain()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsc", "class cScene\n{\n    function play()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        [[self]]->\n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 18));

        Assert.True(HasLabel(entries, "play"));
        Assert.True(HasLabel(entries, "alert"));
    }

    [Fact]
    public void SelfArrow_OffersOnlyMethods()
    {
        // The arrow can ONLY be a method call, so a builtin or a namespace function in this list
        // would be something the syntax cannot reach.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "class cScene\n{\n    function alert()\n    {\n        [[self]]->\n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 18));

        Assert.False(HasLabel(entries, "helper"));
        Assert.False(HasLabel(entries, "GetTime"));
        Assert.All(entries, entry => Assert.Equal(CompletionKind.Function, entry.Kind));
    }

    [Fact]
    public void ArrowOnAnUnknownReceiver_OffersEveryVisibleClassesMethods()
    {
        // 155 of the 159 arrow calls in the stock scripts are this shape, so this is the branch that
        // carries the feature. The receiver's class is unknown, so every candidate is offered.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\a.gsc", "class cScene\n{\n    function play()\n    {\n    }\n}\n")
            .AddFile(@$"{Raw}\scripts\b.gsc", "class cOther\n{\n    function stop()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#namespace game;\nfunction run()\n{\n    [[o_scene]]->\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 17));

        Assert.True(HasLabel(entries, "play"));
        Assert.True(HasLabel(entries, "stop"));
    }

    [Fact]
    public void ArrowOnAnUnknownReceiver_LabelsEachMethodWithItsDeclaringClass()
    {
        // Two classes may declare the same name, and the detail is the only thing telling the two
        // rows apart.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\a.gsc", "class cScene\n{\n    function play()\n    {\n    }\n}\n")
            .AddFile(@$"{Raw}\scripts\b.gsc", "class cOther\n{\n    function play()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#namespace game;\nfunction run()\n{\n    [[o_scene]]->\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 17));
        ImmutableArray<CompletionEntry> plays = [.. entries.Where(e => e.Label.StartsWith("play", StringComparison.Ordinal))];

        Assert.Equal(2, plays.Length);
        Assert.Contains(plays, e => e.Detail == "cScene");
        Assert.Contains(plays, e => e.Detail == "cOther");
    }

    [Fact]
    public void BarePrefixInsideAClassBody_OffersOwnAndInheritedMethods()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsc", "class cScene\n{\n    function play()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        pl\n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(4, 10));

        Assert.True(HasLabel(entries, "play"));
        Assert.True(HasLabel(entries, "alert"));
    }

    [Fact]
    public void BarePrefixInsideAClassBody_StillOffersBuiltinsAndNamespaceFunctions()
    {
        // Method-first must not become method-only: everything reachable from a class body is still
        // reachable, and a list that dropped the engine library would be far worse than the old one.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#using scripts\\util;\n#namespace game;\nclass cScene\n{\n    function alert()\n    {\n        x\n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(6, 9));

        // An imported function is labelled with its qualifier on purpose — the editor filters on the
        // label, and BO3 needs the qualifier written at the call site anyway.
        Assert.True(HasLabel(entries, "util::helper"));
        Assert.True(HasLabel(entries, "GetTime"));
    }

    [Fact]
    public void BarePrefixOutsideAClass_OffersNoMethods()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsc", "class cScene\n{\n    function unique_method_name()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "#namespace game;\nfunction run()\n{\n    un\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(3, 6));

        Assert.False(HasLabel(entries, "unique_method_name"));
    }

    [Fact]
    public void AnOverriddenMethod_IsOfferedOnce()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\base.gsc", "class cScene\n{\n    function play()\n    {\n    }\n}\n");

        CompletionEngine engine = BuildWorld(files);
        string text = "class cAwarenessScene : cScene\n{\n    function play()\n    {\n    }\n    function alert()\n    {\n        [[self]]->\n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(7, 18));

        Assert.Single(entries.Where(e => e.Label.StartsWith("play", StringComparison.Ordinal)));
    }
}
