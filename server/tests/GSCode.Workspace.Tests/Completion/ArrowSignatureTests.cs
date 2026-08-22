using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Signature help for the arrow form.
///
/// It is token-driven, so an arrow call looks exactly like a bare call: the callee token is the
/// method name and there is no <c>::</c> before it. That made <c>[[o_obj]]-&gt;play(</c> resolve
/// through whichever class the CARET was inside, and then — when that missed — through the
/// namespace lookups, which with a null namespace match any namespace at all and landed on
/// <c>animation::play</c>. Both are wrong for the same reason: the receiver of an arrow call is not
/// the enclosing class, and is never a namespace.
/// </summary>
public class ArrowSignatureTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static SignatureEngine BuildEngine(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return new SignatureEngine(database, BuiltinApiSet.Load(ApiDirectory));
    }

    private static ParseResult Analyze(string path, string text)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptAnalysis.LanguageFromPath(path), SourceText.From(text), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>scene_shared.gsc's shape: a class `play`, plus an unrelated `animation::play`.</summary>
    private static FakeFileSystem World()
    {
        return new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\scene.gsc",
                "class cSceneObject\n{\n    function play( str_state )\n    {\n    }\n}\n")
            .AddFile(
                @$"{Raw}\scripts\animation.gsc",
                "#namespace animation;\nfunction play( animation, v_origin, v_angles )\n{\n}\n");
    }

    [Fact]
    public void ArrowCall_DoesNotShowAnUnrelatedNamespaceFunction()
    {
        SignatureEngine engine = BuildEngine(World());

        string text = "#namespace game;\nfunction run()\n{\n    thread [[o_obj]]->play( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        SignatureResult? signature = engine.Resolve(result, "raw", new Position(3, 28));

        Assert.NotNull(signature);
        Assert.Contains("str_state", signature!.Label);
        Assert.DoesNotContain("v_origin", signature.Label);
    }

    [Fact]
    public void ArrowCallInsideAClass_DoesNotResolveThroughTheEnclosingClass()
    {
        // The caret is inside cScene, but `o_obj` is not `self` — so cScene's own `play` is not the
        // answer merely because the cursor happens to be in it.
        FakeFileSystem files = World()
            .AddFile(
                @$"{Raw}\scripts\other.gsc",
                "class cScene\n{\n    function play( a, b, c, d )\n    {\n    }\n}\n");

        SignatureEngine engine = BuildEngine(files);

        string text = "class cScene\n{\n    function run()\n    {\n        [[o_obj]]->play( \n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        SignatureResult? signature = engine.Resolve(result, "raw", new Position(4, 25));

        // Either declaring class is a legitimate candidate; a namespace function is not.
        Assert.NotNull(signature);
        Assert.DoesNotContain("v_origin", signature!.Label);
    }

    [Fact]
    public void SelfArrowCall_ResolvesThroughTheEnclosingClass()
    {
        SignatureEngine engine = BuildEngine(World());

        string text = "class cScene\n{\n    function play( n_alert )\n    {\n    }\n    function run()\n    {\n        [[self]]->play( \n    }\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        SignatureResult? signature = engine.Resolve(result, "raw", new Position(7, 24));

        Assert.NotNull(signature);
        Assert.Contains("n_alert", signature!.Label);
    }

    [Fact]
    public void ArrowCall_DoesNotShowABuiltin()
    {
        // The arrow dispatches on an object, so it can never reach the engine library.
        SignatureEngine engine = BuildEngine(World());

        string text = "#namespace game;\nfunction run()\n{\n    [[o_obj]]->GetTime( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        Assert.Null(engine.Resolve(result, "raw", new Position(3, 24)));
    }

    [Fact]
    public void APlainCallToTheSameName_StillResolvesByNamespace()
    {
        // The narrowing is scoped to the arrow form; an ordinary qualified call is untouched.
        SignatureEngine engine = BuildEngine(World());

        string text = "#using scripts\\animation;\n#namespace game;\nfunction run()\n{\n    animation::play( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);

        SignatureResult? signature = engine.Resolve(result, "raw", new Position(4, 21));

        Assert.NotNull(signature);
        Assert.Contains("v_origin", signature!.Label);
    }
}
