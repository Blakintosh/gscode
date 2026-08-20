using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports statements that can never run because the one before them always leaves the block.
///
/// Only the certain cases. A statement is unreachable when a SIBLING earlier in the same block
/// ends every path out of it — <c>return</c>, <c>break</c> and <c>continue</c> — and nothing can
/// jump back in, since GSC has no labels or gotos. That makes this a syntactic question rather
/// than a dataflow one, and it is why the answer can be trusted.
///
/// Reported as Information, which is the quietest severity VS Code's Problems panel shows. It was
/// a Hint, and a Hint never reaches that panel — and this rule has nothing to fade in the panel's
/// place, since it carries no Unnecessary tag and a greyed run of statements is indistinguishable
/// from a comment. The finding was therefore invisible unless the reader happened to hover it.
///
/// The corpora are what make the panel affordable: 48 findings in 42 files across all five shipped
/// games (BO1 17, BO3 13, WAW 8, MW2 6, CoD4 4) out of roughly 8,300 scripts. That is the test a
/// panel-visible severity has to pass here — 5012 and 5020 stay Hints because theirs run to
/// thousands.
///
/// Not a Warning: the code does no harm, it simply never runs, and the fix is optional.
///
/// Everything from the terminator to the end of the block is one diagnostic, not one per statement.
/// The cause is a single terminator, so five reported statements would be five ways of saying it.
/// </summary>
public static class UnreachableCodeLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        Walk(result.Tree.Root, diagnostics);

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Finds every block in the file. Only <see cref="BlockNode"/> is interesting — the run of
    /// statements after a terminator lives there and nowhere else — so everything else just
    /// descends through <see cref="AstSearch.ChildrenOf"/> rather than being enumerated here.
    ///
    /// Statements NESTED inside a dev block are reached this way and their dead code is still
    /// reported. What is not reported is the dev block's own run against a terminator before it,
    /// which is <see cref="ReportAfterTerminator"/>'s business: <c>/# … #/</c> is compiled out of a
    /// release build, so a debugging aid after a return is something the author put there knowingly
    /// rather than a leftover.
    /// </summary>
    private static void Walk(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // Nothing this rule looks for lives inside an expression: a block is a statement, and GSC
        // has no construct that puts one inside an operand. Descending anyway walked every operand
        // of every expression in the file for nothing, which was most of the rule's cost.
        if ( node is ExprNode )
        {
            return;
        }

        InspectNode(node, diagnostics);

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Walk(child, diagnostics);
        }
    }

    /// <summary>
    /// This rule's whole judgement about ONE node, with no descent of its own, so
    /// <see cref="NodeLintPass"/> can run it from the shared walk. Only ever called for a node that
    /// is not an <c>ExprNode</c>, which is the same restriction the walk above applies.
    /// </summary>
    internal static void InspectNode(AstNode node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ( node is BlockNode block )
        {
            ReportAfterTerminator(block, diagnostics);
        }
    }

    /// <summary>
    /// Reports the run of statements after the first terminator in a block, if any.
    ///
    /// A DEV BLOCK is skipped: <c>/# … #/</c> is compiled out of a release build, so a statement
    /// after a return inside one is a debugging aid the author put there knowingly, and greying it
    /// out would be reporting the dev block itself rather than a mistake.
    /// </summary>
    private static void ReportAfterTerminator(BlockNode block, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        for ( int index = 0; index < block.Statements.Length - 1; index++ )
        {
            if ( !IsTerminator(block.Statements[index]) )
            {
                continue;
            }

            AstNode first = block.Statements[index + 1];
            AstNode last = block.Statements[^1];

            diagnostics.Add(Diagnostic.Create(
                new TextRange(first.Range.Start, last.Range.End),
                DiagnosticSeverity.Information,
                GscDiagnosticCode.UnreachableCode,
                DescribeTerminator(block.Statements[index])));
            return;
        }
    }

    /// <summary>
    /// Whether this statement always leaves the enclosing block.
    ///
    /// Deliberately shallow. An `if` whose arms both return also ends every path, but proving that
    /// means walking into it, and every extra rule is another chance to grey out code that does in
    /// fact run. The three jump statements are certain on their face, and they are what people
    /// actually leave dead code behind.
    /// </summary>
    private static bool IsTerminator(AstNode statement)
    {
        return statement is ReturnNode or BreakNode or ContinueNode;
    }

    private static string DescribeTerminator(AstNode statement)
    {
        return statement switch
        {
            ReturnNode => "return",
            BreakNode => "break",
            _ => "continue",
        };
    }
}
