using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Typing;

/// <summary>
/// Branch-join convergence: the type of a local after a control-flow join is the join of
/// every path that could reach it. The porting ledger's CfaTests / TypeFlowConvergenceTests
/// scenarios, re-expressed against the v2 typer.
/// </summary>
public class TypeFlowConvergenceTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static FlowTyper NewTyper()
    {
        return new FlowTyper(ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc), ObjectFields.Load(ApiDirectory));
    }

    /// <summary>
    /// The joined type of <c>probe_target</c> after the given body, observed by reading it
    /// into a fresh local. Reading it is what exercises the environment: the hint recorded
    /// for <c>sink</c> is whatever flow believes <c>probe_target</c> holds at that point, and
    /// an Unknown type records no hint at all.
    /// </summary>
    private static ScrType TypeOfProbe(string body)
    {
        string source = "function f()\n{\n" + body + "\n    sink = probe_target;\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        ImmutableArray<InferredAssignment> inferred = NewTyper().InferAssignments(result);
        foreach ( InferredAssignment assignment in inferred )
        {
            if ( string.Equals(assignment.Name, "sink", StringComparison.Ordinal) )
            {
                return assignment.Type;
            }
        }

        return ScrType.Unknown;
    }

    [Fact]
    public void BranchesAgreeing_KeepTheType()
    {
        ScrType type = TypeOfProbe(
            "    if ( a )\n    {\n        probe_target = 1;\n    }\n    else\n    {\n        probe_target = 2;\n    }");

        Assert.Equal(ScrType.Int, type);
    }

    [Fact]
    public void BranchesDisagreeing_CollapseToUnknown()
    {
        // The else arm used to win outright because both arms shared one environment.
        ScrType type = TypeOfProbe(
            "    if ( a )\n    {\n        probe_target = 1;\n    }\n    else\n    {\n        probe_target = \"text\";\n    }");

        Assert.Equal(ScrType.Unknown, type);
    }

    [Fact]
    public void IntAndFloatBranches_WidenToFloat()
    {
        ScrType type = TypeOfProbe(
            "    if ( a )\n    {\n        probe_target = 1;\n    }\n    else\n    {\n        probe_target = 2.5;\n    }");

        Assert.Equal(ScrType.Float, type);
    }

    [Fact]
    public void AssignedOnlyInsideAnIf_IsUnknownAfterwards()
    {
        // Without an else the variable may be undefined at the join, so no type is asserted.
        ScrType type = TypeOfProbe("    if ( a )\n    {\n        probe_target = 1;\n    }");

        Assert.Equal(ScrType.Unknown, type);
    }

    [Fact]
    public void ReassignedInsideAnIf_JoinsWithTheOuterValue()
    {
        ScrType type = TypeOfProbe(
            "    probe_target = 1;\n    if ( a )\n    {\n        probe_target = \"text\";\n    }");

        Assert.Equal(ScrType.Unknown, type);
    }

    [Fact]
    public void ReassignedInsideAnIf_ToTheSameType_KeepsIt()
    {
        ScrType type = TypeOfProbe(
            "    probe_target = 1;\n    if ( a )\n    {\n        probe_target = 7;\n    }");

        Assert.Equal(ScrType.Int, type);
    }

    [Fact]
    public void WhileBodyMayNotRun_SoItsAssignmentJoinsWithTheOuterValue()
    {
        ScrType type = TypeOfProbe(
            "    probe_target = 1;\n    while ( a )\n    {\n        probe_target = \"text\";\n    }");

        Assert.Equal(ScrType.Unknown, type);
    }

    [Fact]
    public void DoWhileBodyAlwaysRuns_SoItsAssignmentWins()
    {
        ScrType type = TypeOfProbe(
            "    probe_target = 1;\n    do\n    {\n        probe_target = \"text\";\n    }\n    while ( a );");

        Assert.Equal(ScrType.String, type);
    }

    [Fact]
    public void ForInitializerRunsUnconditionally()
    {
        ScrType type = TypeOfProbe("    for ( probe_target = 0; probe_target < 10; probe_target++ )\n    {\n    }");

        Assert.Equal(ScrType.Int, type);
    }

    [Fact]
    public void SwitchWithoutDefault_JoinsAgainstTheUnmatchedPath()
    {
        // No default means no case need run, so the pre-switch state is a live alternative.
        ScrType type = TypeOfProbe(
            "    probe_target = 1;\n    switch ( a )\n    {\n        case 1:\n            probe_target = \"text\";\n            break;\n    }");

        Assert.Equal(ScrType.Unknown, type);
    }

    [Fact]
    public void SwitchCasesAgreeing_KeepTheType()
    {
        ScrType type = TypeOfProbe(
            "    switch ( a )\n    {\n        case 1:\n            probe_target = 1;\n            break;\n        default:\n            probe_target = 2;\n            break;\n    }");

        Assert.Equal(ScrType.Int, type);
    }

    [Fact]
    public void NestedBranches_ConvergeThroughBothLevels()
    {
        ScrType type = TypeOfProbe(
            "    if ( a )\n    {\n        if ( b )\n        {\n            probe_target = 1;\n        }\n        else\n        {\n            probe_target = 2;\n        }\n    }\n    else\n    {\n        probe_target = 3;\n    }");

        Assert.Equal(ScrType.Int, type);
    }
}
