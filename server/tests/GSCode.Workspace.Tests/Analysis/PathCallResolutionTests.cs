using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// What happens when a path call names a file the distribution does not ship. Distributions really
/// do this: WaW's scripts call into clientscripts\_fx and BO1's into animscripts\shared, neither of
/// which is included, and one absent file accounted for 4,824 calls in WaW alone.
///
/// The rule is one cause, one diagnostic — the missing FILE is reported once, and the calls into it
/// are left alone. The assertion that matters is that the count does NOT scale with the number of
/// call sites, which is the whole reason the behaviour exists.
///
/// Uses CoD4, since a path call is a merge-dialect construct: BO3 has none, so this is unreachable
/// from the profile the other lint tests use.
/// </summary>
public class PathCallResolutionTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");
    private static GameProfile Cod4 => GameProfile.ByName("cod4")!;

    /// <summary>
    /// Lints <paramref name="source"/> as <c>maps\mp\caller.gsc</c>, in a workspace holding one real
    /// utility file which declares exactly one function.
    ///
    /// Indexed under CoD4 explicitly. Left to the default it would be BO3, where a keyword-less
    /// <c>shown()</c> is not a declaration at all, and the store would come back empty with every
    /// assertion here passing for the wrong reason.
    /// </summary>
    private static ImmutableArray<Diagnostic> Lint(string source)
    {
        using TestWorkspace workspace = TestWorkspace.Build(
            [new TestFile(@"maps\mp\_utility.gsc", "shown()\n{\n}\n")], Cod4);

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory, Cod4);
        return FunctionResolutionLint.Analyze(
            workspace.Analyze(@"maps\mp\caller.gsc", source), workspace.Database.Gsc, "raw",
            TestPaths.Raw(@"maps\mp\caller.gsc"), builtins.For(ScriptLanguage.Gsc), Cod4,
            resolver: workspace.Resolver);
    }

    [Fact]
    public void ManyCallsIntoOneMissingFile_ReportThatFileExactlyOnce()
    {
        // Five calls, one absent file. Reporting each would be five errors saying the same thing.
        string source =
            "main()\n{\n"
            + "\tmaps\\mp\\_absent::alpha();\n"
            + "\tmaps\\mp\\_absent::beta();\n"
            + "\tmaps\\mp\\_absent::gamma();\n"
            + "\tmaps\\mp\\_absent::delta();\n"
            + "\tmaps\\mp\\_absent::epsilon();\n"
            + "}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.UsingNotFound, diagnostic.Code);
        Assert.Contains("_absent", diagnostic.Message, StringComparison.OrdinalIgnoreCase);

        // None of the five functions is named: the file is why, and the functions are not actionable.
        Assert.DoesNotContain("alpha", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoMissingFiles_ReportOneDiagnosticEach()
    {
        // The count tracks absent FILES, not call sites.
        string source =
            "main()\n{\n"
            + "\tmaps\\mp\\_absent::alpha();\n"
            + "\tmaps\\mp\\_absent::beta();\n"
            + "\tmaps\\mp\\_missing::gamma();\n"
            + "}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, static d => Assert.Equal(GscDiagnosticCode.UsingNotFound, d.Code));
    }

    [Fact]
    public void AFunctionMissingFromAFileThatEXISTS_IsStillAScriptMiss()
    {
        // The other side of the rule: the file is there, so an unresolved function in it is a real
        // and actionable error rather than a consequence of the distribution.
        string source = "main()\n{\n\tmaps\\mp\\_utility::neverDeclared();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.ScriptFunctionNotFound, diagnostic.Code);
        Assert.Contains("neverDeclared", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AResolvingPathCall_IsNotReported()
    {
        string source = "main()\n{\n\tmaps\\mp\\_utility::shown();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);
        Assert.True(
            diagnostics.IsEmpty,
            "expected none, got: " + string.Join(" | ", diagnostics.Select(static d => $"{d.Code}: {d.Message}")));
    }
}
