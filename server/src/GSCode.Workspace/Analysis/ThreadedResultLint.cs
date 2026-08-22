using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a <c>thread</c> call whose value is used for something.
///
/// A threaded call does not return the function's result. It starts the function on its own thread
/// and hands back control at the first <c>wait</c> inside it, so the caller receives whatever the
/// function had reached by then — which for anything that waits at all is <c>undefined</c>. The
/// trap is that it does not always fail: a threaded function with no wait in it runs to completion
/// before control returns, so the value is correct until somebody adds a wait to a function three
/// files away and every caller silently starts reading undefined.
///
/// 1.5 raised this as two codes. <c>ConsumedThreadedCallResult</c> asked whether a call's result was
/// consumed, and <c>AssignOnThreadedFunction</c> asked whether an assignment's right-hand side
/// contained a thread call — the same mistake, counted twice, so an assignment matched both. This
/// is the first question only, because it is the one that generalises: an argument, a condition, a
/// return value and a wait duration all consume a value, and none of them is an assignment.
///
/// Needs no type information. The distinction is positional: an expression STATEMENT evaluates its
/// expression for effect and discards the value, which is exactly what a threaded call is for.
/// Everywhere else, the value is wanted.
/// </summary>
public static class ThreadedResultLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        WalkStatement(result.Tree.Root, diagnostics);

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Descends the statement forms. An <see cref="ExprStatementNode"/> is the one place a value is
    /// discarded, so its own expression enters unconsumed; every other statement holds expressions
    /// it USES — a condition is tested, a return value handed back, a wait duration counted — so
    /// those enter consumed.
    /// </summary>
    private static void WalkStatement(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is ExprStatementNode statement )
        {
            WalkExpression(Unwrap(statement.Expression), consumed: false, diagnostics);
            return;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            if ( child is ExprNode expression )
            {
                WalkExpression(expression, consumed: true, diagnostics);
                continue;
            }

            WalkStatement(child, diagnostics);
        }
    }

    /// <summary>
    /// Everything below the top of a statement's expression is a value, whatever the top was:
    /// <c>thread spawn( thread build() )</c> starts one thread properly and reads the other's
    /// result, so the argument is still reported.
    /// </summary>
    private static void WalkExpression(
        ExprNode node, bool consumed, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( consumed && node is CallNode { IsThread: true } )
        {
            diagnostics.Add(Diagnostic.Create(
                node.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.ConsumedThreadedCallResult));
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            if ( child is ExprNode childExpression )
            {
                WalkExpression(childExpression, consumed: true, diagnostics);
                continue;
            }

            WalkStatement(child, diagnostics);
        }
    }

    /// <summary>
    /// Parentheses around a whole statement discard the value just as the statement does, so
    /// <c>( thread foo() );</c> is the ordinary form written oddly rather than a finding.
    /// </summary>
    private static ExprNode Unwrap(ExprNode node)
    {
        while ( node is ParenNode paren )
        {
            node = paren.Inner;
        }

        return node;
    }
}
