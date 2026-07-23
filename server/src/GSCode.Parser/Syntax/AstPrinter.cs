using System.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

/// <summary>
/// Renders a syntax tree as a compact S-expression — the golden format for parser tests
/// and a debugging aid. Deterministic; ranges are deliberately omitted.
/// </summary>
public static class AstPrinter
{
    /// <summary>Prints any node (and its subtree) as an S-expression.</summary>
    public static string Print(AstNode node)
    {
        StringBuilder builder = new();
        Write(node, builder);
        return builder.ToString();
    }

    private static void Write(AstNode node, StringBuilder builder)
    {
        switch ( node )
        {
            case ScriptNode script:
                WriteList(builder, "script", script.Elements);
                return;
            case UsingNode usingNode:
                builder.Append("(using \"").Append(usingNode.Path).Append("\")");
                return;
            case NamespaceNode namespaceNode:
                builder.Append("(namespace ").Append(namespaceNode.NameToken.Text).Append(')');
                return;
            case PrecacheNode precache:
            {
                builder.Append("(precache");
                foreach ( GSCode.Parser.Preprocessing.PToken argument in precache.Arguments )
                {
                    if ( argument.Kind != TokenKind.Comma )
                    {
                        builder.Append(' ').Append(argument.Text);
                    }
                }

                builder.Append(')');
                return;
            }
            case UsingAnimTreeNode animTree:
                builder.Append("(using_animtree ").Append(animTree.TreeNameToken?.Text ?? "?").Append(')');
                return;
            case FunctionNode function:
            {
                builder.Append("(function ");
                if ( function.IsPrivate )
                {
                    builder.Append("private ");
                }

                if ( function.IsAutoexec )
                {
                    builder.Append("autoexec ");
                }

                builder.Append(function.NameToken.Text);
                builder.Append(" (params");
                foreach ( ParameterNode parameter in function.Parameters )
                {
                    builder.Append(' ');
                    Write(parameter, builder);
                }

                if ( function.HasVarargs )
                {
                    builder.Append(" ...");
                }

                builder.Append(") ");
                Write(function.Body, builder);
                builder.Append(')');
                return;
            }
            case ParameterNode parameter:
            {
                builder.Append('(');
                if ( parameter.ByRef )
                {
                    builder.Append('&');
                }

                builder.Append(parameter.NameToken.Text);
                if ( parameter.DefaultValue is not null )
                {
                    builder.Append(" = ");
                    Write(parameter.DefaultValue, builder);
                }

                builder.Append(')');
                return;
            }
            case ClassNode classNode:
            {
                builder.Append("(class ").Append(classNode.NameToken.Text);
                if ( classNode.ParentToken is not null )
                {
                    builder.Append(" : ").Append(classNode.ParentToken.Value.Text);
                }

                foreach ( AstNode member in classNode.Members )
                {
                    builder.Append(' ');
                    Write(member, builder);
                }

                builder.Append(')');
                return;
            }
            case VarDeclNode varDecl:
                builder.Append("(var ").Append(varDecl.NameToken.Text).Append(')');
                return;
            case ConstructorNode constructor:
                builder.Append("(constructor ");
                Write(constructor.Body, builder);
                builder.Append(')');
                return;
            case DestructorNode destructor:
                builder.Append("(destructor ");
                Write(destructor.Body, builder);
                builder.Append(')');
                return;
            case DevBlockDeclNode devDecl:
                WriteList(builder, "devblock", devDecl.Declarations);
                return;
            case BlockNode block:
                WriteList(builder, "block", block.Statements);
                return;
            case IfNode ifNode:
            {
                builder.Append("(if ");
                Write(ifNode.Condition, builder);
                builder.Append(' ');
                Write(ifNode.Then, builder);
                if ( ifNode.Else is not null )
                {
                    builder.Append(" else ");
                    Write(ifNode.Else, builder);
                }

                builder.Append(')');
                return;
            }
            case WhileNode whileNode:
                builder.Append("(while ");
                Write(whileNode.Condition, builder);
                builder.Append(' ');
                Write(whileNode.Body, builder);
                builder.Append(')');
                return;
            case DoWhileNode doWhile:
                builder.Append("(do ");
                Write(doWhile.Body, builder);
                builder.Append(" while ");
                Write(doWhile.Condition, builder);
                builder.Append(')');
                return;
            case ForNode forNode:
            {
                builder.Append("(for ");
                WriteOptional(forNode.Initializer, builder);
                builder.Append(' ');
                WriteOptional(forNode.Condition, builder);
                builder.Append(' ');
                WriteOptional(forNode.Increment, builder);
                builder.Append(' ');
                Write(forNode.Body, builder);
                builder.Append(')');
                return;
            }
            case ForeachNode foreachNode:
            {
                builder.Append("(foreach ");
                if ( foreachNode.KeyToken is not null )
                {
                    builder.Append(foreachNode.KeyToken.Value.Text).Append(' ');
                }

                builder.Append(foreachNode.ValueToken.Text).Append(" in ");
                Write(foreachNode.Collection, builder);
                builder.Append(' ');
                Write(foreachNode.Body, builder);
                builder.Append(')');
                return;
            }
            case SwitchNode switchNode:
            {
                builder.Append("(switch ");
                Write(switchNode.Subject, builder);
                foreach ( CaseGroupNode caseGroup in switchNode.Cases )
                {
                    builder.Append(' ');
                    Write(caseGroup, builder);
                }

                builder.Append(')');
                return;
            }
            case CaseGroupNode caseGroup:
            {
                builder.Append("(case");
                foreach ( ExprNode? label in caseGroup.Labels )
                {
                    builder.Append(' ');
                    if ( label is null )
                    {
                        builder.Append("default");
                    }
                    else
                    {
                        Write(label, builder);
                    }
                }

                foreach ( AstNode statement in caseGroup.Statements )
                {
                    builder.Append(' ');
                    Write(statement, builder);
                }

                builder.Append(')');
                return;
            }
            case ReturnNode returnNode:
            {
                builder.Append("(return");
                if ( returnNode.Value is not null )
                {
                    builder.Append(' ');
                    Write(returnNode.Value, builder);
                }

                builder.Append(')');
                return;
            }
            case BreakNode:
                builder.Append("(break)");
                return;
            case ContinueNode:
                builder.Append("(continue)");
                return;
            case WaitNode wait:
                builder.Append(wait.IsRealTime ? "(waitrealtime " : "(wait ");
                Write(wait.Duration, builder);
                builder.Append(')');
                return;
            case WaitTillFrameEndNode:
                builder.Append("(waittillframeend)");
                return;
            case ConstDeclNode constDecl:
                builder.Append("(const ").Append(constDecl.NameToken.Text).Append(" = ");
                Write(constDecl.Value, builder);
                builder.Append(')');
                return;
            case ExprStatementNode exprStatement:
                Write(exprStatement.Expression, builder);
                return;
            case DevBlockStmtNode devStmt:
                WriteList(builder, "devblock", devStmt.Statements);
                return;
            case EmptyStatementNode:
                builder.Append("(empty)");
                return;
            case LiteralNode literal:
                builder.Append(literal.Token.Text);
                return;
            case IdentifierNode identifier:
                builder.Append(identifier.Token.Text);
                return;
            case QualifiedNode qualified:
                builder.Append(qualified.NamespaceToken.Text).Append("::").Append(qualified.NameToken.Text);
                return;
            case PathQualifiedNode path:
                builder.Append(path.Path).Append("::").Append(path.NameToken.Text);
                return;
            case ParenNode paren:
                builder.Append("(paren ");
                Write(paren.Inner, builder);
                builder.Append(')');
                return;
            case VectorNode vector:
                builder.Append("(vector ");
                Write(vector.X, builder);
                builder.Append(' ');
                Write(vector.Y, builder);
                builder.Append(' ');
                Write(vector.Z, builder);
                builder.Append(')');
                return;
            case ArrayLiteralNode:
                builder.Append("(array)");
                return;
            case BinaryNode binary:
                builder.Append('(').Append(TokenFacts.GetStaticText(binary.Operator)).Append(' ');
                Write(binary.Left, builder);
                builder.Append(' ');
                Write(binary.Right, builder);
                builder.Append(')');
                return;
            case TernaryNode ternary:
                builder.Append("(?: ");
                Write(ternary.Condition, builder);
                builder.Append(' ');
                Write(ternary.WhenTrue, builder);
                builder.Append(' ');
                Write(ternary.WhenFalse, builder);
                builder.Append(')');
                return;
            case PrefixNode prefix:
                builder.Append("(prefix").Append(TokenFacts.GetStaticText(prefix.Operator)).Append(' ');
                Write(prefix.Operand, builder);
                builder.Append(')');
                return;
            case PostfixNode postfix:
                builder.Append("(postfix").Append(TokenFacts.GetStaticText(postfix.Operator)).Append(' ');
                Write(postfix.Operand, builder);
                builder.Append(')');
                return;
            case AssignmentNode assignment:
                builder.Append('(').Append(TokenFacts.GetStaticText(assignment.Operator)).Append(' ');
                Write(assignment.Target, builder);
                builder.Append(' ');
                Write(assignment.Value, builder);
                builder.Append(')');
                return;
            case MemberNode member:
                builder.Append("(. ");
                Write(member.Object, builder);
                builder.Append(' ').Append(member.NameToken.Text).Append(')');
                return;
            case IndexNode index:
                builder.Append("(index ");
                Write(index.Object, builder);
                builder.Append(' ');
                Write(index.Index, builder);
                builder.Append(')');
                return;
            case PointerDerefNode pointer:
                builder.Append("(deref ");
                Write(pointer.Pointer, builder);
                builder.Append(')');
                return;
            case CallNode call:
            {
                builder.Append("(call");
                if ( call.IsThread )
                {
                    builder.Append(" thread");
                }

                if ( call.Target is not null )
                {
                    builder.Append(" on:");
                    Write(call.Target, builder);
                }

                builder.Append(' ');
                Write(call.Callee, builder);
                foreach ( ExprNode argument in call.Arguments )
                {
                    builder.Append(' ');
                    Write(argument, builder);
                }

                builder.Append(')');
                return;
            }
            case ArrowCallNode arrow:
            {
                builder.Append("(-> ");
                Write(arrow.Object, builder);
                builder.Append(' ').Append(arrow.MethodToken.Text);
                foreach ( ExprNode argument in arrow.Arguments )
                {
                    builder.Append(' ');
                    Write(argument, builder);
                }

                builder.Append(')');
                return;
            }
            case NewNode newNode:
                builder.Append("(new ").Append(newNode.ClassToken.Text).Append(')');
                return;
            case ErrorNode:
                builder.Append("(error)");
                return;
            default:
                builder.Append("(?").Append(node.GetType().Name).Append(')');
                return;
        }
    }

    private static void WriteOptional(AstNode? node, StringBuilder builder)
    {
        if ( node is null )
        {
            builder.Append('_');
            return;
        }

        Write(node, builder);
    }

    private static void WriteList(StringBuilder builder, string label, System.Collections.Immutable.ImmutableArray<AstNode> children)
    {
        builder.Append('(').Append(label);
        foreach ( AstNode child in children )
        {
            builder.Append(' ');
            Write(child, builder);
        }

        builder.Append(')');
    }
}
