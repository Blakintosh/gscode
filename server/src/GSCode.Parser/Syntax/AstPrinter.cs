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
    /// <summary>
    /// Ceiling on how deep this walker will descend, counted in TREE LEVELS.
    ///
    /// The parser's own <c>MaxNestingDepth</c> caps a tree at 513 levels on the theory that every
    /// recursive consumer inherits the bound. This walker is the one that does not: its frames are
    /// the fattest in the project — a single switch over ~40 node shapes, so the frame carries every
    /// pattern-match local at once — and it recurses a frame per level with no loops.
    ///
    /// Measured on a thread with the platform default 1 MB stack (what the server's thread-pool
    /// threads and xunit's test threads both get), printing <c>x = a.b.b…</c> at the parser's cap:
    ///
    ///   Release   513 levels survives; the same tree on a 512 KB stack does not
    ///   Debug     240 levels survives, 242 does not
    ///
    /// So the inherited bound holds only in Release, and only just. In Debug it does not hold at
    /// all, which is not a test-only concern dressed up as one: a StackOverflowException cannot be
    /// caught, so the failure is the whole process, and the configuration that first proved it was
    /// <c>dotnet test</c> aborting its own run.
    ///
    /// 128 is under half the tightest measured cliff and about four times inside the Release one.
    /// Nothing hand-written comes near it — the parser's 512-entry cap is only ~170 nested
    /// parentheses, and this counts levels of the tree those produce.
    /// </summary>
    private const int MaxPrintDepth = 128;

    /// <summary>
    /// Stands in for a subtree past <see cref="MaxPrintDepth"/>. Deliberately not a shape the
    /// grammar can produce, so it cannot be mistaken for a node that was really there.
    ///
    /// Two distinct subtrees that differ only past the ceiling print the same text, which matters
    /// to the one caller using the output as an identity — <c>CaseLabelLint</c>'s duplicate check.
    /// A label nested 128 deep has already been reported as <c>NestingTooDeep</c> and abandoned, so
    /// the file is failing louder elsewhere either way.
    /// </summary>
    private const string TooDeep = "(...)";

    /// <summary>Prints any node (and its subtree) as an S-expression.</summary>
    public static string Print(AstNode node)
    {
        StringBuilder builder = new();
        Write(node, builder, 1);
        return builder.ToString();
    }

    private static void Write(AstNode node, StringBuilder builder, int depth)
    {
        if ( depth > MaxPrintDepth )
        {
            builder.Append(TooDeep);
            return;
        }

        switch ( node )
        {
            case ScriptNode script:
                WriteList(builder, "script", script.Elements, depth);
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
                    Write(parameter, builder, depth + 1);
                }

                if ( function.HasVarargs )
                {
                    builder.Append(" ...");
                }

                builder.Append(") ");
                Write(function.Body, builder, depth + 1);
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
                    Write(parameter.DefaultValue, builder, depth + 1);
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
                    Write(member, builder, depth + 1);
                }

                builder.Append(')');
                return;
            }
            case VarDeclNode varDecl:
                builder.Append("(var ").Append(varDecl.NameToken.Text).Append(')');
                return;
            case ConstructorNode constructor:
                builder.Append("(constructor ");
                Write(constructor.Body, builder, depth + 1);
                builder.Append(')');
                return;
            case DestructorNode destructor:
                builder.Append("(destructor ");
                Write(destructor.Body, builder, depth + 1);
                builder.Append(')');
                return;
            case DevBlockDeclNode devDecl:
                WriteList(builder, "devblock", devDecl.Declarations, depth);
                return;
            case BlockNode block:
                WriteList(builder, "block", block.Statements, depth);
                return;
            case IfNode ifNode:
            {
                builder.Append("(if ");
                Write(ifNode.Condition, builder, depth + 1);
                builder.Append(' ');
                Write(ifNode.Then, builder, depth + 1);
                if ( ifNode.Else is not null )
                {
                    builder.Append(" else ");
                    Write(ifNode.Else, builder, depth + 1);
                }

                builder.Append(')');
                return;
            }
            case WhileNode whileNode:
                builder.Append("(while ");
                Write(whileNode.Condition, builder, depth + 1);
                builder.Append(' ');
                Write(whileNode.Body, builder, depth + 1);
                builder.Append(')');
                return;
            case DoWhileNode doWhile:
                builder.Append("(do ");
                Write(doWhile.Body, builder, depth + 1);
                builder.Append(" while ");
                Write(doWhile.Condition, builder, depth + 1);
                builder.Append(')');
                return;
            case ForNode forNode:
            {
                builder.Append("(for ");
                WriteOptional(forNode.Initializer, builder, depth + 1);
                builder.Append(' ');
                WriteOptional(forNode.Condition, builder, depth + 1);
                builder.Append(' ');
                WriteOptional(forNode.Increment, builder, depth + 1);
                builder.Append(' ');
                Write(forNode.Body, builder, depth + 1);
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
                Write(foreachNode.Collection, builder, depth + 1);
                builder.Append(' ');
                Write(foreachNode.Body, builder, depth + 1);
                builder.Append(')');
                return;
            }
            case SwitchNode switchNode:
            {
                builder.Append("(switch ");
                Write(switchNode.Subject, builder, depth + 1);
                foreach ( CaseGroupNode caseGroup in switchNode.Cases )
                {
                    builder.Append(' ');
                    Write(caseGroup, builder, depth + 1);
                }

                builder.Append(')');
                return;
            }
            case CaseGroupNode caseGroup:
            {
                builder.Append("(case");
                foreach ( CaseLabel label in caseGroup.Labels )
                {
                    builder.Append(' ');
                    if ( label.Value is null )
                    {
                        builder.Append("default");
                    }
                    else
                    {
                        Write(label.Value, builder, depth + 1);
                    }
                }

                foreach ( AstNode statement in caseGroup.Statements )
                {
                    builder.Append(' ');
                    Write(statement, builder, depth + 1);
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
                    Write(returnNode.Value, builder, depth + 1);
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
                Write(wait.Duration, builder, depth + 1);
                builder.Append(')');
                return;
            case WaitTillFrameEndNode:
                builder.Append("(waittillframeend)");
                return;
            case ConstDeclNode constDecl:
                builder.Append("(const ").Append(constDecl.NameToken.Text).Append(" = ");
                Write(constDecl.Value, builder, depth + 1);
                builder.Append(')');
                return;
            case ExprStatementNode exprStatement:
                Write(exprStatement.Expression, builder, depth + 1);
                return;
            case DevBlockStmtNode devStmt:
                WriteList(builder, "devblock", devStmt.Statements, depth);
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
                Write(paren.Inner, builder, depth + 1);
                builder.Append(')');
                return;
            case VectorNode vector:
                builder.Append("(vector ");
                Write(vector.X, builder, depth + 1);
                builder.Append(' ');
                Write(vector.Y, builder, depth + 1);
                builder.Append(' ');
                Write(vector.Z, builder, depth + 1);
                builder.Append(')');
                return;
            case ArrayLiteralNode:
                builder.Append("(array)");
                return;
            case BinaryNode binary:
                builder.Append('(').Append(TokenFacts.GetStaticText(binary.Operator)).Append(' ');
                Write(binary.Left, builder, depth + 1);
                builder.Append(' ');
                Write(binary.Right, builder, depth + 1);
                builder.Append(')');
                return;
            case TernaryNode ternary:
                builder.Append("(?: ");
                Write(ternary.Condition, builder, depth + 1);
                builder.Append(' ');
                Write(ternary.WhenTrue, builder, depth + 1);
                builder.Append(' ');
                Write(ternary.WhenFalse, builder, depth + 1);
                builder.Append(')');
                return;
            case PrefixNode prefix:
                builder.Append("(prefix").Append(TokenFacts.GetStaticText(prefix.Operator)).Append(' ');
                Write(prefix.Operand, builder, depth + 1);
                builder.Append(')');
                return;
            case PostfixNode postfix:
                builder.Append("(postfix").Append(TokenFacts.GetStaticText(postfix.Operator)).Append(' ');
                Write(postfix.Operand, builder, depth + 1);
                builder.Append(')');
                return;
            case AssignmentNode assignment:
                builder.Append('(').Append(TokenFacts.GetStaticText(assignment.Operator)).Append(' ');
                Write(assignment.Target, builder, depth + 1);
                builder.Append(' ');
                Write(assignment.Value, builder, depth + 1);
                builder.Append(')');
                return;
            case MemberNode member:
                builder.Append("(. ");
                Write(member.Object, builder, depth + 1);
                builder.Append(' ').Append(member.NameToken.Text).Append(')');
                return;
            case IndexNode index:
                builder.Append("(index ");
                Write(index.Object, builder, depth + 1);
                builder.Append(' ');
                Write(index.Index, builder, depth + 1);
                builder.Append(')');
                return;
            case PointerDerefNode pointer:
                builder.Append("(deref ");
                Write(pointer.Pointer, builder, depth + 1);
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
                    Write(call.Target, builder, depth + 1);
                }

                builder.Append(' ');
                Write(call.Callee, builder, depth + 1);
                foreach ( ExprNode argument in call.Arguments )
                {
                    builder.Append(' ');
                    Write(argument, builder, depth + 1);
                }

                builder.Append(')');
                return;
            }
            case ArrowCallNode arrow:
            {
                builder.Append("(-> ");
                Write(arrow.Object, builder, depth + 1);
                builder.Append(' ').Append(arrow.MethodToken.Text);
                foreach ( ExprNode argument in arrow.Arguments )
                {
                    builder.Append(' ');
                    Write(argument, builder, depth + 1);
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

    private static void WriteOptional(AstNode? node, StringBuilder builder, int depth)
    {
        if ( node is null )
        {
            builder.Append('_');
            return;
        }

        Write(node, builder, depth);
    }

    private static void WriteList(StringBuilder builder, string label, System.Collections.Immutable.ImmutableArray<AstNode> children, int depth)
    {
        builder.Append('(').Append(label);
        foreach ( AstNode child in children )
        {
            builder.Append(' ');
            Write(child, builder, depth + 1);
        }

        builder.Append(')');
    }
}
