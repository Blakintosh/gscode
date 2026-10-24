using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The #include counterpart to <see cref="UnusedUsingLintTests"/>: an #include contributing nothing
/// this file calls is a greyed-out hint. #include is a merge-dialect import, so the workspace is
/// indexed as CoD4: under the default BO3 a bare <c>helper()</c> is not a declaration at all.
/// </summary>
public class UnusedIncludeLintTests
{

    /// <summary>A hub declaring nothing of its own, reaching utility only by including it.</summary>
    private const string ChainSource = "#include common_scripts\\utility;\n";
    private static readonly GameProfile s_cod4 = GameProfile.ByName("cod4")!;

    private static ImmutableArray<Diagnostic> Lint(string askingSource)
    {
        // A hub that declares nothing itself and exists only to pull utility in — the shape a
        // marginal test has to get right.
        using TestWorkspace workspace = TestWorkspace.Build(
            [
                new TestFile(@"common_scripts\utility.gsc", "helper()\n{\n}\n"),
                new TestFile(@"maps\_chain.gsc", ChainSource),
                new TestFile(@"maps\_chain2.gsc", ChainSource),
            ],
            s_cod4);

        return UnusedIncludeLint.Analyze(
            workspace.Analyze(@"scripts\main.gsc", askingSource), workspace.Database.Gsc, ScriptLanguage.Gsc,
            workspace.Resolver, TestPaths.Raw(@"scripts\main.gsc"));
    }

    [Fact]
    public void FlagsAnIncludeWhoseFunctionsAreNeverCalled()
    {
        Diagnostic hint = Assert.Single(Lint("#include common_scripts\\utility;\nrun()\n{\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Contains(DiagnosticTag.Unnecessary, hint.Tags);
        Assert.Contains("utility", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAnIncludeWhoseFunctionIsCalled()
    {
        Assert.Empty(Lint("#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n"));
    }

    [Fact]
    public void AnIncludeUsedOnlyByAPathCall_IsUnused()
    {
        // A path call loads the file it names on its own; the include adds nothing to it.
        Diagnostic hint = Assert.Single(Lint(
            "#include common_scripts\\utility;\nrun()\n{\n\tcommon_scripts\\utility::helper();\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
    }

    [Fact]
    public void AHubIncludedPurelyAsAConduit_IsUnused()
    {
        // A hub passes nothing on: the compiler copies only the included file's own functions. The
        // stock file that looked like it relied on one, maps\_createpath.gsc reaching flag_init
        // through maps\_utility, is named by no other script and never compiled.
        Diagnostic hint = Assert.Single(Lint("#include maps\\_chain;\nrun()\n{\n\thelper();\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
    }

    [Fact]
    public void StillFlagsAHubWhoseContentsAreReachedAnotherWay()
    {
        // The other half, and why membership in the closure is not enough: including a hub AND the
        // file beneath it is routine in the stock scripts, and there the hub really is redundant. On
        // CoD4 this distinction is 33 directives for maps\_utility alone.
        Diagnostic hint = Assert.Single(Lint(
            "#include maps\\_chain;\n#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
        Assert.Contains("_chain", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoHubsThatOnlyIncludeTheDeclaringFile_AreBothUnused()
    {
        // Neither hub supplies helper: an include merges the included file's OWN functions, not what
        // it got from its own includes, so the call is out of scope
        // with or without them, and 5026 says so.
        ImmutableArray<Diagnostic> hints = Lint(
            "#include maps\\_chain;\n#include maps\\_chain2;\nrun()\n{\n\thelper();\n}\n");

        Assert.Equal(2, hints.Count(static d => d.Code == GscDiagnosticCode.UnusedInclude));
    }

    [Fact]
    public void SaysNothingWhenThereAreNoIncludes()
    {
        Assert.Empty(Lint("run()\n{\n\thelper();\n}\n"));
    }

    [Fact]
    public void AnUnreadableIncludeIsNotJudged()
    {
        // A missing target is UsingNotFound's job. The directive never enters Includes, so this
        // rule still never reports both a missing AND an unused include for one line — what
        // changed is that the file's OTHER includes are judged now instead of the pass standing
        // down wholesale.
        Assert.Empty(Lint("#include scripts\\does_not_exist;\nrun()\n{\n}\n"));
    }

    [Fact]
    public void AnUnreadableIncludeNoLongerSpares_ItsSiblings()
    {
        // The narrowing, stated as the behaviour change: this file used to be told nothing at all
        // because one of its two includes could not be read. The readable one supplies nothing
        // called here, and that verdict never depended on the unreadable one.
        Diagnostic diagnostic = Assert.Single(Lint(
            "#include scripts\\does_not_exist;\n#include common_scripts\\utility;\nrun()\n{\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, diagnostic.Code);
        Assert.Equal(1, diagnostic.Range.Start.Line);
    }
}
