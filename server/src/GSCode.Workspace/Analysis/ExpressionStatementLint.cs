using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// A statement whose expression cannot do anything — <c>a + b;</c>, <c>self.health;</c>,
/// <c>x == 1;</c>. The value is computed and dropped, so the line has no effect at all, and the
/// usual cause is a missing <c>=</c> or a call that lost its parentheses.
///
/// The test is deliberately the WEAKEST one that still catches those: a statement is reported only
/// when its expression contains no effectful node ANYWHERE in it. Deciding effectfulness from the
/// top node alone would be tighter and wrong in both directions that matter — <c>a ? foo() : bar();</c>
/// is a ternary that calls, and <c>flag &amp;&amp; start();</c> is a binary that calls. Both run
/// something; neither has an effectful node on top. GSC gives no compiler to contradict a false
/// report here, so the rule gives up precision to keep every report certain.
/// </summary>
public static class ExpressionStatementLint
{
    private static bool HasParseError(ParseResult result)
    {
        foreach ( Diagnostic diagnostic in result.Tree.Diagnostics )
        {
            if ( (int)diagnostic.Code is >= 3000 and < 4000 )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// This rule's whole judgement about ONE node, with no descent of its own, so
    /// <see cref="NodeLintPass"/> can run it from the shared walk. The caller is responsible for
    /// the parse-error gate — see <see cref="Applies"/>.
    /// </summary>
    internal static void InspectNode(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // A for-loop's initializer and increment are ExprStatementNodes too, and the same rule
        // applies to them: `for ( i = 0; i < 3; i )` increments nothing.
        if ( node is ExprStatementNode statement && !HasEffect(statement.Expression) )
        {
            diagnostics.Add(Diagnostic.Create(
                statement.Expression.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.InvalidExpressionStatement));
        }
    }

    /// <summary>
    /// Whether this rule speaks about this file at all. It stands down on a file the parser could
    /// not read, and the corpus is the entire argument for that.
    ///
    /// Before this gate the rule reported nine statements across the five games and not one was a
    /// statement with no effect — every single one was the wreckage of a parse the tree had
    /// recovered from:
    ///
    /// <list type="bullet">
    /// <item>bo3's two are the known `gib.gsc(58)` grammar gap, where an object-like macro is called
    /// as <c>GET_GIB_BUNDLES()</c> and the postfix chain will not accept the '('.</item>
    /// <item>bo1's four are <c>level.scr_anim[…] = % o_full_interstitial_01_camera;</c> — an anim
    /// reference written with a space after '%', which splits into an assignment and a bare
    /// identifier, and the identifier is what got reported.</item>
    /// </list>
    ///
    /// The rule's premise is that the statement is what the author wrote. After a parse error the
    /// tree is a recovery guess, so the premise does not hold and neither does the finding — which
    /// would also be a second diagnostic on a line that already has one.
    ///
    /// Called by <see cref="NodeLintPass"/>, which asks once per file and then skips the rule for
    /// every node rather than re-testing it at each one.
    /// </summary>
    internal static bool Applies(ParseResult result)
    {
        return !HasParseError(result);
    }

    /// <summary>
    /// Whether anything in the expression can change state or run code. Calls and object
    /// construction can; assignment and <c>++</c>/<c>--</c> do by definition. Everything else
    /// computes a value, and at statement position that value goes nowhere.
    /// </summary>
    private static bool HasEffect(ExprNode node)
    {
        if ( node is CallNode or ArrowCallNode or AssignmentNode or PostfixNode or NewNode )
        {
            return true;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            if ( child is ExprNode expression && HasEffect(expression) )
            {
                return true;
            }
        }

        return false;
    }
}
