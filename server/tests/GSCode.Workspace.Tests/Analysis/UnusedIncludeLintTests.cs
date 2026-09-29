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
    public void KeepsAnIncludeUsedByAPathCall()
    {
        // maps\...::helper is keyed (null, helper) too, so a path call counts as using it.
        Assert.Empty(Lint("#include common_scripts\\utility;\nrun()\n{\n\tcommon_scripts\\utility::helper();\n}\n"));
    }

    [Fact]
    public void KeepsAHubIncludedPurelyAsAConduit()
    {
        // The case that made this test marginal rather than direct. maps\_createpath.gsc reaches
        // flag_init through maps\_utility and includes nothing else; judging the directive by what
        // its TARGET declares called that unused and offered "Remove", and taking the offer broke the
        // file — 5026 then reports the call as out of scope. A Hint whose fix manufactures an Error
        // is worse than either rule being wrong on its own.
        Assert.Empty(Lint("#include maps\\_chain;\nrun()\n{\n\thelper();\n}\n"));
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
    public void TwoConduitsCoveringEachOtherAreBothKept()
    {
        // The trap in judging each directive against the others: helper arrives through both chains,
        // so neither is the SOLE supplier and an independent test calls both removable. Each removal
        // is safe alone and the pair is not — and "Remove all N unused #include directives" takes the
        // pair. Measured against what is certainly kept instead, neither qualifies.
        Assert.Empty(Lint(
            "#include maps\\_chain;\n#include maps\\_chain2;\nrun()\n{\n\thelper();\n}\n"));
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
