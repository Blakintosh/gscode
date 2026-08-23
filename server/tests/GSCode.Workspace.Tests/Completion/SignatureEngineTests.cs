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

public class SignatureEngineTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static SignatureEngine BuildEngine(FakeFileSystem files)
    {
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();
        return new SignatureEngine(database, BuiltinApiSet.Load(ApiDirectory));
    }

    private static ParseResult Analyze(string path, string text)
    {
        return Analyze(path, text, NullInsertProvider.Instance);
    }

    private static ParseResult Analyze(string path, string text, IInsertProvider inserts)
    {
        return ScriptAnalysis.Analyze(path, ScriptAnalysis.LanguageFromPath(path), SourceText.From(text), inserts, new NameTable());
    }

    [Fact]
    public void ScriptFunction_ShowsParametersAndActiveIndex()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction give( weapon, ammo )\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        // Cursor after the comma -> active parameter 1 (ammo).
        string text = "function run()\n{\n    util::give( \"x\", \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterComma = new(2, 20);

        SignatureResult? signature = engine.Resolve(result, "raw", afterComma);

        Assert.NotNull(signature);
        Assert.Equal(2, signature!.Parameters.Length);
        Assert.Equal("weapon", signature.Parameters[0].Label);
        Assert.Equal("ammo", signature.Parameters[1].Label);
        Assert.Equal(1, signature.ActiveParameter);
    }

    [Fact]
    public void Builtin_ResolvesSignature()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "function run()\n{\n    x = Abs( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(2, 12);

        SignatureResult? signature = engine.Resolve(result, "raw", afterParen);

        Assert.NotNull(signature);
        Assert.StartsWith("Abs(", signature!.Label, StringComparison.Ordinal);
        Assert.Single(signature.Parameters);
    }

    [Fact]
    public void OutsideCall_ReturnsNull()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "function run()\n{\n    x = 1;\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position notInCall = new(2, 9);

        Assert.Null(engine.Resolve(result, "raw", notInCall));
    }

    // --- Macros ---
    //
    // A function-like #define is a call with named arguments and nothing described them: the panel
    // answered nothing at all, in a body and at file scope alike. The names come from the parse in
    // hand rather than the store, which is what makes a header inserted a keystroke ago count.

    [Fact]
    public void Macro_ShowsParametersAndActiveIndex()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "#define GIVE( weapon, ammo ) self giveweapon( weapon )\nfunction run()\n{\n    GIVE( \"x\", \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterComma = new(3, 15);

        SignatureResult? signature = engine.Resolve(result, "raw", afterComma);

        Assert.NotNull(signature);
        Assert.Equal("GIVE(weapon, ammo)", signature!.Label);
        Assert.Equal(1, signature.ActiveParameter);
    }

    /// <summary>
    /// The reported case: a macro from an <c>#insert</c>ed header, invoked at column 0. Both halves
    /// were missing — file scope is where the shipped scripts write these, and the header is where
    /// the macro lives.
    /// </summary>
    [Fact]
    public void Macro_FromAnInsertedHeader_ResolvesAtFileScope()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);
        FakeInserts inserts = new FakeInserts()
            .Add(
                @"scripts\shared\shared.gsh",
                "#define REGISTER_SYSTEM( sys, func, reqs ) function autoexec __init__system__() { } // Registers a system.\n");

        string text = "#insert scripts\\shared\\shared.gsh;\n#namespace game;\nREGISTER_SYSTEM( \"aat\", \nfunction run()\n{\n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text, inserts);
        Position afterComma = new(2, 24);

        SignatureResult? signature = engine.Resolve(result, "raw", afterComma);

        Assert.NotNull(signature);
        Assert.Equal("REGISTER_SYSTEM(sys, func, reqs)", signature!.Label);
        Assert.Equal(1, signature.ActiveParameter);

        // The panel carries the define form, what it expands to, and the trailing comment — the same
        // markdown hover renders, so the two cannot drift apart.
        Assert.Contains("#define REGISTER_SYSTEM(sys, func, reqs)", signature.Documentation, StringComparison.Ordinal);
        Assert.Contains("autoexec", signature.Documentation, StringComparison.Ordinal);
        Assert.Contains("Registers a system.", signature.Documentation, StringComparison.Ordinal);
    }

    /// <summary>
    /// An object-like macro is not a call, so it must not answer with an empty signature and take
    /// the position away from whatever the name would otherwise resolve to.
    /// </summary>
    [Fact]
    public void ObjectLikeMacro_IsNotACall()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "#define MAX_PLAYERS 18\nfunction run()\n{\n    x = MAX_PLAYERS( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(3, 21);

        Assert.Null(engine.Resolve(result, "raw", afterParen));
    }

    /// <summary>
    /// Macro names are the language's one case-SENSITIVE kind, so the lookup is ordinal: the
    /// preprocessor would not expand <c>is_true(</c> either, and answering with IS_TRUE's parameters
    /// would describe an expansion that never happens.
    /// </summary>
    [Fact]
    public void Macro_LookupIsCaseSensitive()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "#define IS_TRUE( value ) isdefined( value ) && value\nfunction run()\n{\n    x = is_true( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(3, 17);

        Assert.Null(engine.Resolve(result, "raw", afterParen));
    }

    /// <summary>A qualified name is a namespace member, and the preprocessor expands no such thing.</summary>
    [Fact]
    public void QualifiedName_DoesNotReachAMacro()
    {
        FakeFileSystem files = new FakeFileSystem().AddFile(@$"{Raw}\scripts\d.gsc", "function d()\n{\n}\n");
        SignatureEngine engine = BuildEngine(files);

        string text = "#define IS_TRUE( value ) isdefined( value ) && value\nfunction run()\n{\n    x = util::IS_TRUE( \n}\n";
        ParseResult result = Analyze(@$"{Raw}\scripts\main.gsc", text);
        Position afterParen = new(3, 23);

        Assert.Null(engine.Resolve(result, "raw", afterParen));
    }
}

