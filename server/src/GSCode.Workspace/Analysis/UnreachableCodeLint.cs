using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
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
/// Reported as a Hint with the Unnecessary tag, so the editor greys the code out rather than
/// adding a problem. Dead code is usually a leftover, and the useful thing is to SEE it; an error
/// on something that does no harm would be nagging.
///
/// Everything from the terminator to the end of the block is one diagnostic, not one per statement.
/// The cause is a single terminator, so five greyed statements would be five ways of saying it.
/// </summary>
public static class UnreachableCodeLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            Walk(element, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void Walk(AstNode? node, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( node )
        {
            case null:
                return;

            case BlockNode block:
                ReportAfterTerminator(block, diagnostics);
                foreach ( AstNode statement in block.Statements )
                {
                    Walk(statement, diagnostics);
                }

                return;

            case FunctionNode function:
                Walk(function.Body, diagnostics);
                return;

            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    Walk(member, diagnostics);
                }

                return;

            case ConstructorNode constructor:
                Walk(constructor.Body, diagnostics);
                return;

            case DestructorNode destructor:
                Walk(destructor.Body, diagnostics);
                return;

            case IfNode ifNode:
                Walk(ifNode.Then, diagnostics);
                Walk(ifNode.Else, diagnostics);
                return;

            case WhileNode whileNode:
                Walk(whileNode.Body, diagnostics);
                return;

            case DoWhileNode doWhile:
                Walk(doWhile.Body, diagnostics);
                return;

            case ForNode forNode:
                Walk(forNode.Body, diagnostics);
                return;

            case ForeachNode foreachNode:
                Walk(foreachNode.Body, diagnostics);
                return;

            case SwitchNode switchNode:
                foreach ( CaseGroupNode group in switchNode.Cases )
                {
                    foreach ( AstNode statement in group.Statements )
                    {
                        Walk(statement, diagnostics);
                    }
                }

                return;

            case DevBlockStmtNode devBlock:
                // Statements NESTED inside the block are walked, so dead code within it is still
                // reported. The block's own run is not checked against a terminator before it,
                // which is the point of ReportAfterTerminator skipping dev blocks: `/# … #/` is
                // compiled out of a release build, so a debugging aid after a return is something
                // the author put there knowingly rather than a leftover.
                foreach ( AstNode statement in devBlock.Statements )
                {
                    Walk(statement, diagnostics);
                }

                return;

            default:
                return;
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
                DiagnosticSeverity.Hint,
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
