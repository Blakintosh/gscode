using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Runs ONE per-node lint rule over a file, the way <c>NodeLintPass</c> runs all nine.
///
/// Each of those rules used to carry a public <c>Analyze</c> that walked the tree itself, and after
/// the walks were merged nothing outside these tests called any of them. That left every rule with
/// two walkers: the one the server runs, and the one the tests proved. They agreed by inspection
/// only — <c>CaseLabelLint</c>'s rule about not descending into expressions was written once in its
/// own walk and again as a guard in the shared pass — and if they had drifted the tests would have
/// gone on passing, because they exercised the walk production does not use.
///
/// So the walkers are gone and this drives <c>InspectNode</c> directly. The descent here is the same
/// three lines the shared pass uses, which is the point: a rule's tests now fail when the rule's
/// judgement about a node changes, and nothing else.
/// </summary>
internal static class NodeLintHarness
{
    /// <summary>What one rule asks of one node.</summary>
    internal delegate void Inspect(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics);

    /// <summary>
    /// Every node of the file, in the shared pass's order.
    /// </summary>
    internal static ImmutableArray<Diagnostic> Run(ParseResult result, Inspect inspect)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Visit(result.Tree.Root, inspect, statementsOnly: false, diagnostics);
        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Statements only — the two rules whose subject cannot appear inside an expression, and which
    /// the shared pass therefore does not ask about one. The descent still covers the whole tree, so
    /// this skips the QUESTION rather than the walk, exactly as the pass does.
    /// </summary>
    internal static ImmutableArray<Diagnostic> RunOnStatements(ParseResult result, Inspect inspect)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Visit(result.Tree.Root, inspect, statementsOnly: true, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void Visit(
        AstNode node, Inspect inspect, bool statementsOnly, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( !statementsOnly || node is not ExprNode )
        {
            inspect(node, diagnostics);
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Visit(child, inspect, statementsOnly, diagnostics);
        }
    }
}
