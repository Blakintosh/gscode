using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Dev blocks are stripped from a release build, so calling into one from ordinary code works
/// while developing and breaks only once the mod ships.
/// </summary>
public class DevBlockCallLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string askingSource, FakeFileSystem? extra = null)
    {
        FakeFileSystem files = extra ?? new FakeFileSystem();
        files.AddFile(TestPaths.Raw(@"scripts\placeholder.gsc"), "function p()\n{\n}\n");

        using TestWorkspace workspace = TestWorkspace.Build(files, mode: IndexingMode.Partial);
        ScriptDatabase database = workspace.Database;

        string askingPath = TestPaths.Raw(@"scripts\main.gsc");
        ParseResult result = TestParse.Analyze(askingSource, askingPath);

        // The asking file is not indexed, so commit it too — its own dev-only functions must be
        // resolvable for the same-file case.
        database.Commit(result, ResolutionContext.RawContext, false, @"scripts\main.gsc");

        BuiltinApiSet builtins = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"));

        return DevBlockCallLint.Analyze(
            result,
            database.Gsc,
            "raw",
            askingPath,
            result.Extraction.DeclaredNamespaces,
            builtins.For(ScriptLanguage.Gsc));
    }

    [Fact]
    public void CallingADevOnlyFunctionFromReleaseCode_IsReported()
    {
        // The reported shape.
        string source = "/#\nfunction foo()\n{\n}\n#/\nfunction bar()\n{\n    foo();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.DevOnlyFunctionCalledFromRelease, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("foo", diagnostic.Message);
    }

    [Fact]
    public void TheReport_PointsAtTheDevBlockDeclaration()
    {
        string source = "/#\nfunction foo()\n{\n}\n#/\nfunction bar()\n{\n    foo();\n}\n";

        DiagnosticRelation relation = Assert.Single(Assert.Single(Lint(source)).RelatedInformation);

        Assert.Equal(1, relation.Range.Start.Line);
    }

    [Fact]
    public void CallingFromInsideADevBlock_IsFine()
    {
        // Both sides vanish together in a release build, so the call is consistent.
        string source = "/#\nfunction foo()\n{\n}\nfunction dev_caller()\n{\n    foo();\n}\n#/\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void CallingFromAStatementLevelDevBlock_IsFine()
    {
        // The guard is a dev block INSIDE an ordinary function.
        string source = "/#\nfunction foo()\n{\n}\n#/\nfunction bar()\n{\n    /#\n    foo();\n    #/\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void CallingAnOrdinaryFunction_IsFine()
    {
        string source = "function foo()\n{\n}\nfunction bar()\n{\n    foo();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void DevOnlyFunctionInAnotherFile_IsAlsoReported()
    {
        // The callee's dev-ness is a stored fact, so the check crosses files.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(TestPaths.Raw(@"scripts\devtools.gsc"), "#namespace devtools;\n/#\nfunction dump_state()\n{\n}\n#/\n");

        string source = "#using scripts\\devtools;\n#namespace game;\nfunction run()\n{\n    devtools::dump_state();\n}\n";

        Assert.Equal(
            GscDiagnosticCode.DevOnlyFunctionCalledFromRelease,
            Assert.Single(Lint(source, files)).Code);
    }

    [Fact]
    public void UnknownFunction_IsNotReported()
    {
        // "No such function" is a different problem and must not be mislabelled.
        string source = "function bar()\n{\n    not_a_real_function();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void DevOnlyBuiltinCalledFromReleaseCode_IsReported()
    {
        string source = "function bar()\n{\n    PrintLn( \"hi\" );\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.DevOnlyFunctionCalledFromRelease, diagnostic.Code);
        Assert.Contains("PrintLn", diagnostic.Message);

        // The engine owns builtins, so there is no declaration to point at.
        Assert.Empty(diagnostic.RelatedInformation);
    }

    [Fact]
    public void DevOnlyBuiltinInsideADevBlock_IsFine()
    {
        string source = "function bar()\n{\n    /#\n    PrintLn( \"hi\" );\n    #/\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ReleaseBuiltins_AreNeverFlagged()
    {
        // IPrintLn is the in-game HUD print and exists in release, unlike PrintLn. Confusing
        // the two would flag working code, so the distinction is pinned.
        string source = "function bar()\n{\n    IPrintLn( \"hi\" );\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void TheFlagIsCarriedOnTheFunction_NotQueriedFromTheList()
    {
        // The plumbing that matters: the loader stamps IsDevOnly onto the BuiltinFunction, so
        // the lint reads one property and never consults the curated list directly. When the
        // API data eventually carries its own devOnly field, nothing here has to change.
        BuiltinApi api = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api")).For(ScriptLanguage.Gsc);

        Assert.True(api.Find("PrintLn")!.IsDevOnly);
        Assert.True(api.Find("Line")!.IsDevOnly);
        Assert.False(api.Find("IPrintLn")!.IsDevOnly);
    }

    [Fact]
    public void TheFlagIsCaseInsensitive()
    {
        // GSC identifiers are case-insensitive, and the API even ships Print3d and Print3D as
        // separate entries, so every spelling must resolve to the same answer.
        BuiltinApi api = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api")).For(ScriptLanguage.Gsc);

        Assert.True(api.Find("println")!.IsDevOnly);
        Assert.True(api.Find("PRINTLN")!.IsDevOnly);
    }

    [Fact]
    public void CandidatesContradictedByStockCode_AreExcluded()
    {
        // Both descriptions call these debug instruments, but stock scripts call them OUTSIDE
        // dev blocks and never inside, so listing them would flag shipped code. Pinned so the
        // corpus-validated decision is not undone by someone reading the description.
        Assert.False(DevOnlyBuiltins.Contains("PixMarker"));
        Assert.False(DevOnlyBuiltins.Contains("InfoVolumeDebugInit"));
    }

    [Fact]
    public void Cod4SaysSoInItsOwnData_AndOverridesTheSharedList()
    {
        // The curated list is BO3's, and CoD4 contradicts it: `println` is called 438 times outside
        // a /# #/ dev block there against 220 inside, the inverse of BO3's 2:269. Applying BO3's
        // answer reported 598 Errors across 107 shipped files.
        //
        // The correction lives in CoD4's OWN library rather than by weakening the shared list, and
        // this asserts the loader honours that ordering — entry.DevOnly wins over the fallback.
        BuiltinApi cod4 = ApiLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "Api"), ScriptLanguage.Gsc, GameProfile.ByName("cod4")!);

        foreach ( string name in (string[])["println", "print3d", "line", "print"] )
        {
            BuiltinFunction? function = cod4.Find(name);
            Assert.NotNull(function);
            Assert.False(function!.IsDevOnly, $"{name} is not dev-only in CoD4; its data says so");

            // The shared list still claims it, which is what makes the override load-bearing.
            Assert.True(DevOnlyBuiltins.Contains(name));
        }
    }

    [Fact]
    public void BlackOps3StillTakesTheSharedList()
    {
        // The fallback is the whole point for a game whose data states nothing, so correcting CoD4
        // must not have cost BO3 the check.
        BuiltinApi bo3 = ApiLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "Api"), ScriptLanguage.Gsc, GameProfile.BlackOps3);

        Assert.True(bo3.Find("println")!.IsDevOnly);
    }

    [Fact]
    public void ReleaseOverloadElsewhere_SuppressesTheReport()
    {
        // A same-named function that survives a release build makes the call safe, so the
        // dev-only declaration alone must not condemn it.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(TestPaths.Raw(@"scripts\shared.gsc"), "#namespace shared;\nfunction helper()\n{\n}\n");

        string source = "#using scripts\\shared;\n#namespace shared;\n/#\nfunction helper()\n{\n}\n#/\n"
            + "function run()\n{\n    helper();\n}\n";

        Assert.Empty(Lint(source, files));
    }

    [Fact]
    public void ADevOnlyCallAMacroExpandedInto_IsReported()
    {
        // The macro hides the call, not the consequence: `foo` is stripped from a release build,
        // so the file invoking HELP() is the one that stops compiling once the mod ships.
        string source = "/#\nfunction foo()\n{\n}\n#/\n#define HELP() foo()\nfunction bar()\n{\n    HELP();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.DevOnlyFunctionCalledFromRelease, diagnostic.Code);
        Assert.Equal(8, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void ADevOnlyCallFromAMacroInvokedInsideADevBlock_IsFine()
    {
        // What decides whether the call survives is where the MACRO WAS INVOKED — the expansion
        // lands there — so an invocation inside /# #/ disappears alongside its target.
        string source = "/#\nfunction foo()\n{\n}\n#/\n#define HELP() foo()\nfunction bar()\n{\n    /#\n    HELP();\n    #/\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void ADevOnlyCallAMacroMakesTwice_IsReportedOnce()
    {
        string source = "/#\nfunction foo()\n{\n}\n#/\n#define HELP() foo(); foo()\nfunction bar()\n{\n    HELP();\n}\n";

        Assert.Single(Lint(source));
    }

    [Fact]
    public void AnInheritedMethodSharingItsNameWithADevOnlyFunction_IsNotReported()
    {
        // The shape that shipped broken. scene_shared.gsc calls `error( cond, msg )` thirteen times
        // inside cSceneObject, meaning the bool-returning method it inherits from
        // cScriptBundleObjectBase — and BO3 also declares a dev-block `util::error( msg )` in both
        // mp/_util.gsc and zm/_util.gsc. The rule looked the name up as a namespace function, which
        // cannot see methods and reads a null namespace as "any namespace", so all thirteen calls
        // were reported as shipped-build failures against a function they never reach.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                TestPaths.Raw(@"scripts\bundle.gsc"),
                "#namespace bundle;\nclass cBundleBase\n{\n    function error( condition, msg )\n    {\n    }\n}\n")
            .AddFile(
                TestPaths.Raw(@"scripts\util.gsc"),
                "#namespace util;\n/#\nfunction error( msg )\n{\n}\n#/\n");

        string source = "#using scripts\\bundle;\n#using scripts\\util;\n#namespace scene;\n"
            + "class cSceneObject : cBundleBase\n{\n    function play()\n    {\n"
            + "        error( 1, \"no animation\" );\n    }\n}\n";

        Assert.Empty(Lint(source, files));
    }

    [Fact]
    public void ABareCallInsideAClassThatIsNoMethod_StillFallsBackToTheNamespace()
    {
        // The other half of the routing decision, and what stops the fix from being a blanket
        // "skip anything written inside a class". A bare name that no class in the chain declares
        // means the namespace function, and a dev-only one is exactly as broken here as anywhere.
        string source = "#namespace game;\n/#\nfunction dump_state()\n{\n}\n#/\n"
            + "class cThing\n{\n    function run()\n    {\n        dump_state();\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.DevOnlyFunctionCalledFromRelease, diagnostic.Code);
        Assert.Contains("dump_state", diagnostic.Message);
    }

    [Fact]
    public void AMethodInheritedFromAClassInsideADevBlock_IsReported()
    {
        // Routing gains the rule a case it could never see before: the whole class is stripped from
        // a release build, so the derived class's call to an inherited method stops compiling. The
        // parser takes /# #/ only around a whole class — a dev block inside a class body is not a
        // class member — so this is the only shape a dev-only method comes in.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                TestPaths.Raw(@"scripts\devbase.gsc"),
                "#namespace devbase;\n/#\nclass cDevBase\n{\n    function dump_state()\n    {\n    }\n}\n#/\n");

        string source = "#using scripts\\devbase;\n#namespace game;\n"
            + "class cThing : cDevBase\n{\n    function run()\n    {\n        dump_state();\n    }\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source, files));

        Assert.Equal(GscDiagnosticCode.DevOnlyFunctionCalledFromRelease, diagnostic.Code);
        Assert.Contains("dump_state", diagnostic.Message);
    }

    [Fact]
    public void TheRelation_PointsAtTheHeaderWhenTheDevOnlyFunctionArrivedThroughAnInsert()
    {
        // scripts\devbase.gsc does not write `dump_state` itself — it #inserts a header that
        // does. The resulting FunctionSymbol's NameRange is a TRUE position in THAT header, not
        // in scripts\devbase.gsc, so the related-information path has to follow DeclaringPath
        // there too: pairing the header-true range with the including file's path pointed the
        // relation at whatever text happens to sit at that line and column in devbase.gsc —
        // nothing to do with where `dump_state` is actually declared.
        string headerPath = TestPaths.Raw(@"scripts\devbase_impl.gsh");
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(headerPath, "#namespace devbase;\n/#\nfunction dump_state()\n{\n}\n#/\n")
            .AddFile(TestPaths.Raw(@"scripts\devbase.gsc"), "#insert scripts\\devbase_impl.gsh;\n");

        string source = "#using scripts\\devbase;\n#namespace game;\n"
            + "function run()\n{\n    devbase::dump_state();\n}\n";

        DiagnosticRelation relation = Assert.Single(Assert.Single(Lint(source, files)).RelatedInformation);

        Assert.Equal(PathUtil.NormalizeAbsolute(headerPath), relation.FilePath, ignoreCase: true);
    }
}
