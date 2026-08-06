using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a local that is assigned and never read — <c>function f() { bar = undefined; }</c>.
///
/// Hint severity with the Unnecessary tag, deliberately. Dead code is worth knowing about but is
/// not a defect: the script runs, and half-finished work in progress is the normal reason to have
/// one. Anything louder would be nagging someone mid-edit.
///
/// It was Information, which put every one in the editor's problem list — 1,716 of them over MW2's
/// scripts alone, and 4,711 across the five games, all in code that ships and works. A list that
/// long is one nobody reads. The tag is what carries the finding: the editor greys the name either
/// way, so the signal survives and only the list entry goes. Every other rule of this kind here
/// (5020, 5012, 5001, 5015, 5002) was already a Hint.
///
/// Reads and writes are told apart structurally rather than by counting occurrences. A name is
/// READ wherever it appears except as the direct target of a plain <c>=</c>; a compound assignment
/// (<c>+=</c>) reads its target, and so does <c>x++</c>, which is why those do not count as
/// dead stores.
///
/// Only plain locals are considered. <c>self.foo</c> and <c>level.bar</c> are fields with lives of
/// their own — another script may read them — so an unread write to one says nothing.
/// </summary>
public static class UnusedLocalLint
{
    public static ImmutableArray<Diagnostic> Analyze(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            CollectFromDeclaration(element, diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void CollectFromDeclaration(AstNode element, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        switch ( element )
        {
            case FunctionNode function:
                InspectFunction(function, diagnostics);
                return;
            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members )
                {
                    CollectFromDeclaration(member, diagnostics);
                }

                return;
            case DevBlockDeclNode devBlock:
                foreach ( AstNode declaration in devBlock.Declarations )
                {
                    CollectFromDeclaration(declaration, diagnostics);
                }

                return;
            default:
                return;
        }
    }

    private static void InspectFunction(FunctionNode function, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // First write per name, in source order, and every name ever read.
        Dictionary<string, PToken> firstWrite = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> read = new(StringComparer.OrdinalIgnoreCase);

        // A parameter is not a dead store: the caller supplied it, and an unread one is a
        // different finding with a different rule.
        foreach ( ParameterNode parameter in function.Parameters )
        {
            read.Add(parameter.NameToken.Text);
        }

        Walk(function.Body, firstWrite, read);

        foreach ( KeyValuePair<string, PToken> write in firstWrite )
        {
            if ( read.Contains(write.Key) )
            {
                continue;
            }

            // Macro-supplied names are not the author's to remove, and the range would point at
            // the invocation rather than at anything they wrote.
            if ( write.Value.Provenance.DefinitionSite is not null )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                write.Value.RootRange,
                DiagnosticSeverity.Hint,
                GscDiagnosticCode.UnusedLocal,
                write.Value.Text);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }
    }

    private static void Walk(AstNode? node, Dictionary<string, PToken> firstWrite, HashSet<string> read)
    {
        switch ( node )
        {
            case null:
                return;
            case BlockNode block:
                foreach ( AstNode statement in block.Statements )
                {
                    Walk(statement, firstWrite, read);
                }

                return;
            case DevBlockStmtNode devBlock:
                foreach ( AstNode statement in devBlock.Statements )
                {
                    Walk(statement, firstWrite, read);
                }

                return;
            case IfNode ifNode:
                WalkExpression(ifNode.Condition, firstWrite, read);
                Walk(ifNode.Then, firstWrite, read);
                Walk(ifNode.Else, firstWrite, read);
                return;
            case WhileNode whileNode:
                WalkExpression(whileNode.Condition, firstWrite, read);
                Walk(whileNode.Body, firstWrite, read);
                return;
            case DoWhileNode doWhile:
                Walk(doWhile.Body, firstWrite, read);
                WalkExpression(doWhile.Condition, firstWrite, read);
                return;
            case ForNode forNode:
                Walk(forNode.Initializer, firstWrite, read);
                WalkExpression(forNode.Condition, firstWrite, read);
                Walk(forNode.Increment, firstWrite, read);
                Walk(forNode.Body, firstWrite, read);
                return;
            case ForeachNode foreachNode:
                // A loop variable is bound by the loop, not assigned by the author, and an unused
                // `key` in `foreach ( key, value in … )` is idiomatic rather than dead.
                if ( foreachNode.KeyToken is not null )
                {
                    read.Add(foreachNode.KeyToken.Value.Text);
                }

                read.Add(foreachNode.ValueToken.Text);
                WalkExpression(foreachNode.Collection, firstWrite, read);
                Walk(foreachNode.Body, firstWrite, read);
                return;
            case SwitchNode switchNode:
                WalkExpression(switchNode.Subject, firstWrite, read);
                foreach ( CaseGroupNode group in switchNode.Cases )
                {
                    foreach ( ExprNode? label in group.Labels )
                    {
                        WalkExpression(label, firstWrite, read);
                    }

                    foreach ( AstNode statement in group.Statements )
                    {
                        Walk(statement, firstWrite, read);
                    }
                }

                return;
            case ReturnNode returnNode:
                WalkExpression(returnNode.Value, firstWrite, read);
                return;
            case WaitNode wait:
                WalkExpression(wait.Duration, firstWrite, read);
                return;
            case ConstDeclNode constDecl:
                RecordWrite(constDecl.NameToken, firstWrite);
                WalkExpression(constDecl.Value, firstWrite, read);
                return;
            case ExprStatementNode expression:
                WalkExpression(expression.Expression, firstWrite, read);
                return;
            default:
                return;
        }
    }

    private static void WalkExpression(ExprNode? expression, Dictionary<string, PToken> firstWrite, HashSet<string> read)
    {
        switch ( expression )
        {
            case null:
                return;
            case AssignmentNode assignment:
            {
                // `x = value` writes x. `x += value` READS x as well, so it can never be a dead
                // store on its own.
                if ( assignment.Target is IdentifierNode target )
                {
                    if ( assignment.Operator == TokenKind.Assign )
                    {
                        RecordWrite(target.Token, firstWrite);
                    }
                    else
                    {
                        read.Add(target.Token.Text);
                    }
                }
                else
                {
                    // self.foo = … — a field, whose reader may be another script entirely.
                    WalkExpression(assignment.Target, firstWrite, read);
                }

                WalkExpression(assignment.Value, firstWrite, read);
                return;
            }
            case IdentifierNode identifier:
                read.Add(identifier.Token.Text);
                return;
            case PostfixNode postfix:
                // x++ reads and writes in one step.
                WalkExpression(postfix.Operand, firstWrite, read);
                return;
            case PrefixNode prefix:
                WalkExpression(prefix.Operand, firstWrite, read);
                return;
            case ParenNode paren:
                WalkExpression(paren.Inner, firstWrite, read);
                return;
            case BinaryNode binary:
                WalkExpression(binary.Left, firstWrite, read);
                WalkExpression(binary.Right, firstWrite, read);
                return;
            case TernaryNode ternary:
                WalkExpression(ternary.Condition, firstWrite, read);
                WalkExpression(ternary.WhenTrue, firstWrite, read);
                WalkExpression(ternary.WhenFalse, firstWrite, read);
                return;
            case VectorNode vector:
                WalkExpression(vector.X, firstWrite, read);
                WalkExpression(vector.Y, firstWrite, read);
                WalkExpression(vector.Z, firstWrite, read);
                return;
            case MemberNode member:
                WalkExpression(member.Object, firstWrite, read);
                return;
            case IndexNode index:
                WalkExpression(index.Object, firstWrite, read);
                WalkExpression(index.Index, firstWrite, read);
                return;
            case PointerDerefNode pointer:
                WalkExpression(pointer.Pointer, firstWrite, read);
                return;
            case CallNode call:
                // Target is the object a method is called ON — `self` in `self foo()`.
                WalkExpression(call.Target, firstWrite, read);

                // The callee of `foo()` names a FUNCTION, so it is not a read of a local called
                // foo. `[[ handler ]]()` is different: that really does read the local.
                if ( call.Callee is not (IdentifierNode or QualifiedNode or PathQualifiedNode) )
                {
                    WalkExpression(call.Callee, firstWrite, read);
                }

                foreach ( ExprNode argument in call.Arguments )
                {
                    WalkExpression(argument, firstWrite, read);
                }

                return;
            case ArrowCallNode arrow:
                WalkExpression(arrow.Object, firstWrite, read);
                foreach ( ExprNode argument in arrow.Arguments )
                {
                    WalkExpression(argument, firstWrite, read);
                }

                return;
            case NewNode newNode:
                foreach ( ExprNode argument in newNode.Arguments )
                {
                    WalkExpression(argument, firstWrite, read);
                }

                return;
            default:
                return;
        }
    }

    private static void RecordWrite(PToken nameToken, Dictionary<string, PToken> firstWrite)
    {
        // The FIRST write is the one reported: it is where the name is introduced, and a later
        // one is only dead because the first was too.
        if ( !firstWrite.ContainsKey(nameToken.Text) )
        {
            firstWrite[nameToken.Text] = nameToken;
        }
    }
}
