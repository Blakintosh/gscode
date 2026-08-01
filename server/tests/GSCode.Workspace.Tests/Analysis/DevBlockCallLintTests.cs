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
/// Dev blocks are stripped from a release build, so calling into one from ordinary code works
/// while developing and breaks only once the mod ships.
/// </summary>
public class DevBlockCallLintTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static ImmutableArray<Diagnostic> Lint(string askingSource, FakeFileSystem? extra = null)
    {
        FakeFileSystem files = extra ?? new FakeFileSystem();
        files.AddFile(@$"{Raw}\scripts\placeholder.gsc", "function p()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        string askingPath = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(askingSource), NullInsertProvider.Instance, new NameTable());

        // The asking file is not indexed, so commit it too — its own dev-only functions must be
        // resolvable for the same-file case.
        database.Commit(result, ResolutionContext.RawContext, false, @"scripts\main.gsc");

        BuiltinApiSet builtins = BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api"));

        return DevBlockCallLint.Analyze(
            result,
            database.Gsc,
            "raw",
            askingPath,
            DatabaseQueries.DeclaredNamespaces(result),
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
            .AddFile(@$"{Raw}\scripts\devtools.gsc", "#namespace devtools;\n/#\nfunction dump_state()\n{\n}\n#/\n");

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
            .AddFile(@$"{Raw}\scripts\shared.gsc", "#namespace shared;\nfunction helper()\n{\n}\n");

        string source = "#using scripts\\shared;\n#namespace shared;\n/#\nfunction helper()\n{\n}\n#/\n"
            + "function run()\n{\n    helper();\n}\n";

        Assert.Empty(Lint(source, files));
    }
}
