using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// What the lints say about class methods, now that they resolve.
///
/// Two suppressions used to stand in for resolution here, and both were blunt: the resolution lint
/// silenced EVERY unresolved namespace-less call on a dialect with classes (because an arrow call
/// and a <c>sys::</c> builtin call were keyed identically), and the argument-count lint saw no
/// candidates for a method and skipped it. Removing them is only safe if the cases below hold, so
/// they are pinned here as well as being swept over the stock scripts.
/// </summary>
public class ClassMethodLintTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ScriptDatabase Workspace(params (string Name, string Source)[] files)
    {
        FakeFileSystem system = new();
        foreach ( (string Name, string Source) file in files )
        {
            system.AddFile(@$"{Raw}\scripts\{file.Name}.gsc", file.Source);
        }

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], system);
        PathResolver resolver = new(config, system);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, system, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return database;
    }

    private static ParseResult Parse(string source, string path)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Resolution diagnostics for <paramref name="source"/>, with the other files indexed.</summary>
    private static ImmutableArray<Diagnostic> Resolution(
        string source, params (string Name, string Source)[] others)
    {
        string path = @$"{Raw}\scripts\main.gsc";
        ScriptDatabase database = Workspace([("main", source), .. others]);
        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);

        return FunctionResolutionLint.Analyze(
            Parse(source, path), database.Gsc, "raw", path, builtins.For(ScriptLanguage.Gsc), GameProfile.BlackOps3);
    }

    /// <summary>Argument-count diagnostics for <paramref name="source"/>.</summary>
    private static ImmutableArray<Diagnostic> Arity(string source, params (string Name, string Source)[] others)
    {
        string path = @$"{Raw}\scripts\main.gsc";
        ScriptDatabase database = Workspace([("main", source), .. others]);
        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);

        return ArgumentCountLint.Analyze(
            Parse(source, path), database.Gsc, "raw", path, builtins.For(ScriptLanguage.Gsc), GameProfile.BlackOps3);
    }

    // --- Resolution ---

    [Fact]
    public void BareCallToAnOwnMethod_IsNotReported()
    {
        string source = "class cScene\n{\n    function play()\n    {\n    }\n    function run()\n    {\n        play();\n    }\n}\n";

        Assert.Empty(Resolution(source));
    }

    [Fact]
    public void BareCallToAnInheritedMethod_IsNotReported()
    {
        string source = "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        play();\n    }\n}\n";

        Assert.Empty(Resolution(source, ("base", "class cScene\n{\n    function play()\n    {\n    }\n}\n")));
    }

    [Fact]
    public void QualifiedCallToAClassMethod_IsNotReported()
    {
        string source = "#namespace main;\nfunction run()\n{\n    o cScene::play();\n}\n";

        Assert.Empty(Resolution(source, ("base", "class cScene\n{\n    function play()\n    {\n    }\n}\n")));
    }

    [Fact]
    public void ArrowCallToAKnownMethod_IsNotReported()
    {
        string source = "#namespace main;\nfunction run()\n{\n    [[o_scene]]->play();\n}\n";

        Assert.Empty(Resolution(source, ("base", "class cScene\n{\n    function play()\n    {\n    }\n}\n")));
    }

    [Fact]
    public void ArrowCallToAMethodNoClassDeclares_IsReported()
    {
        // The one namespace-less shape that CAN be judged on a dialect with classes: no builtin and
        // no namespace function can be written [[x]]->name(), so reaching here means it exists
        // nowhere. This is precisely what the blanket suppression used to hide.
        string source = "#namespace main;\nfunction run()\n{\n    [[o_scene]]->no_such_method();\n}\n";

        Diagnostic diagnostic = Assert.Single(Resolution(source, ("base", "class cScene\n{\n}\n")));
        Assert.Equal(GscDiagnosticCode.ScriptFunctionNotFound, diagnostic.Code);
    }

    [Fact]
    public void AnUnknownNameInAClassBearingFile_IsStillReported()
    {
        // The suppression's real cost: a file that declared a class silenced every unresolved
        // namespace-less call in it, so genuine typos went unreported wherever classes were used.
        string source = "class cScene\n{\n    function run()\n    {\n        BuiltInDoesNotExist();\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(Resolution(source));
        Assert.Equal(GscDiagnosticCode.BuiltinFunctionNotFound, diagnostic.Code);
    }

    [Fact]
    public void ABuiltinCalledInsideAClassBody_IsNotReported()
    {
        // Method-first must not become method-only: an engine call inside a class body still has to
        // reach the builtin library.
        string source = "class cScene\n{\n    function run()\n    {\n        n = GetTime();\n    }\n}\n";

        Assert.Empty(Resolution(source));
    }

    // --- Argument count ---

    [Fact]
    public void TooManyArgumentsToAnOwnMethod_IsReported()
    {
        string source = "class cScene\n{\n    function play( a )\n    {\n    }\n    function run()\n    {\n        play( 1, 2, 3 );\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(Arity(source));
        Assert.Equal(GscDiagnosticCode.TooManyArguments, diagnostic.Code);
    }

    [Fact]
    public void FewerArgumentsToAMethod_IsNotReported()
    {
        // Same rule as for a function: the missing ones are undefined, and this is idiomatic.
        string source = "class cScene\n{\n    function play( a, b )\n    {\n    }\n    function run()\n    {\n        play( 1 );\n    }\n}\n";

        Assert.Empty(Arity(source));
    }

    [Fact]
    public void AnInheritedMethod_UsesTheAncestorsArity()
    {
        string source = "class cAwarenessScene : cScene\n{\n    function alert()\n    {\n        play( 1, 2 );\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(
            Arity(source, ("base", "class cScene\n{\n    function play( a )\n    {\n    }\n}\n")));

        Assert.Equal(GscDiagnosticCode.TooManyArguments, diagnostic.Code);
    }

    [Fact]
    public void AMethodWithVarargs_TakesAnything()
    {
        string source = "class cScene\n{\n    function play( a, ... )\n    {\n    }\n    function run()\n    {\n        play( 1, 2, 3, 4 );\n    }\n}\n";

        Assert.Empty(Arity(source));
    }

    [Fact]
    public void TooManyArgumentsToAnArrowCall_IsReported()
    {
        string source = "#namespace main;\nfunction run()\n{\n    [[o_scene]]->play( 1, 2 );\n}\n";

        Diagnostic diagnostic = Assert.Single(
            Arity(source, ("base", "class cScene\n{\n    function play( a )\n    {\n    }\n}\n")));

        Assert.Equal(GscDiagnosticCode.TooManyArguments, diagnostic.Code);
    }

    [Fact]
    public void AnArrowCallWithSeveralPossibleDeclarers_IsNotJudged()
    {
        // Several signatures, and picking one to judge against would be a guess.
        string source = "#namespace main;\nfunction run()\n{\n    [[o_scene]]->play( 1, 2, 3 );\n}\n";

        Assert.Empty(Arity(
            source,
            ("a", "class cScene\n{\n    function play( a )\n    {\n    }\n}\n"),
            ("b", "class cOther\n{\n    function play( a, b, c )\n    {\n    }\n}\n")));
    }

    [Fact]
    public void AMethodSharingABuiltinName_IsJudgedAgainstTheMethod()
    {
        // Inside a class the bare name reaches the method, so judging it against the engine's
        // signature for `spawn` would compare the call to something it never calls.
        string source = "class cScene\n{\n    function spawn( a )\n    {\n    }\n    function run()\n    {\n        spawn( 1, 2 );\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(Arity(source));
        Assert.Equal(GscDiagnosticCode.TooManyArguments, diagnostic.Code);
    }

    // --- Namespace import ---

    [Fact]
    public void AClassQualifierNamingARealMethod_IsNotReportedUnimported()
    {
        // No #using can import a class, so a class qualifier must never be called an unimported
        // namespace. All 23 times this lint fired on the stock scripts, this was why.
        string source = "#namespace main;\nfunction run()\n{\n    o cScene::play();\n}\n";
        string path = @$"{Raw}\scripts\main.gsc";

        ScriptDatabase database = Workspace(
            ("main", source), ("base", "class cScene\n{\n    function play()\n    {\n    }\n}\n"));

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], new FakeFileSystem());
        Assert.Empty(NamespaceUsageLint.Analyze(
            Parse(source, path), database.Gsc, ScriptLanguage.Gsc, new PathResolver(config, new FakeFileSystem()), path));
    }

    [Fact]
    public void AClassQualifierNamingNoSuchMethod_IsReported()
    {
        // The strict form. The old name-only test called this fine, so a typo against a real class
        // had nowhere at all to surface.
        string source = "#namespace main;\nfunction run()\n{\n    o cScene::no_such_thing();\n}\n";
        string path = @$"{Raw}\scripts\main.gsc";

        ScriptDatabase database = Workspace(
            ("main", source), ("base", "class cScene\n{\n    function play()\n    {\n    }\n}\n"));

        RootConfig config = RootConfig.Create(true, Raw, @"C:\bo3\mods", [], new FakeFileSystem());
        Diagnostic diagnostic = Assert.Single(NamespaceUsageLint.Analyze(
            Parse(source, path), database.Gsc, ScriptLanguage.Gsc, new PathResolver(config, new FakeFileSystem()), path));

        Assert.Equal(GscDiagnosticCode.NamespaceNotImported, diagnostic.Code);
    }
}
