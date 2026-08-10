using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

/// <summary>
/// Position-based tree queries: the ancestor chain of nodes containing a position
/// (outermost first) — the basis of selection ranges and position-anchored features.
/// </summary>
public static class AstSearch
{
    /// <summary>All nodes whose range contains the position, outermost → innermost.</summary>
    public static List<AstNode> ChainAt(ScriptNode root, Position position)
    {
        List<AstNode> chain = [];
        AstNode current = root;
        chain.Add(root);

        while ( true )
        {
            AstNode? next = null;
            foreach ( AstNode child in ChildrenOf(current) )
            {
                if ( child.Range.Contains(position) )
                {
                    next = child;
                    break;
                }
            }

            if ( next is null )
            {
                return chain;
            }

            chain.Add(next);
            current = next;
        }
    }

    /// <summary>
    /// The innermost identifier under <paramref name="position"/> and the function enclosing it —
    /// the two things every question about a LOCAL starts from, since a local means nothing without
    /// the function that scopes it.
    ///
    /// Shared because hover ("what type is this?") and go-to-definition ("where did this come
    /// from?") were each walking the chain and picking the same two nodes out of it.
    /// </summary>
    public static bool TryFindLocalContext(
        ScriptNode root, Position position, out IdentifierNode identifier, out FunctionNode function)
    {
        IdentifierNode? foundIdentifier = null;
        FunctionNode? foundFunction = null;

        foreach ( AstNode node in ChainAt(root, position) )
        {
            if ( node is FunctionNode enclosing )
            {
                foundFunction = enclosing;
            }
            else if ( node is IdentifierNode candidate )
            {
                foundIdentifier = candidate;
            }
        }

        identifier = foundIdentifier!;
        function = foundFunction!;
        return foundIdentifier is not null && foundFunction is not null;
    }

    /// <summary>
    /// Whether an arrow call's receiver is the bare word <c>self</c> — the shape that makes a method
    /// call resolvable against the enclosing class rather than against every class declaring the name.
    /// </summary>
    public static bool IsSelfReceiver(ExprNode receiver)
    {
        return receiver is IdentifierNode identifier && TokenFacts.IsSelfName(identifier.Token.Text);
    }

    /// <summary>
    /// Whether a callee is the <c>waittill</c> family, whose trailing arguments are BOUND rather
    /// than read — the distinction any rule about reads or unused names has to make.
    ///
    /// A callable keyword parses as an <see cref="IdentifierNode"/> wrapping the keyword token, so
    /// the TOKEN KIND is what distinguishes it from a call to a function sharing the name.
    /// </summary>
    public static bool IsWaittill(ExprNode callee)
    {
        return callee is IdentifierNode identifier
            && identifier.Token.Kind is TokenKind.WaitTill or TokenKind.WaitTillMatch;
    }

    /// <summary>Direct structural children of a node (expression operands included).</summary>
    public static IEnumerable<AstNode> ChildrenOf(AstNode node)
    {
        switch ( node )
        {
            case ScriptNode script:
                foreach ( AstNode element in script.Elements ) { yield return element; }
                yield break;
            case FunctionNode function:
                foreach ( ParameterNode parameter in function.Parameters ) { yield return parameter; }
                yield return function.Body;
                yield break;
            case ParameterNode parameter:
                if ( parameter.DefaultValue is not null ) { yield return parameter.DefaultValue; }
                yield break;
            case ClassNode classNode:
                foreach ( AstNode member in classNode.Members ) { yield return member; }
                yield break;
            case ConstructorNode constructor:
                foreach ( ParameterNode parameter in constructor.Parameters ) { yield return parameter; }
                yield return constructor.Body;
                yield break;
            case DestructorNode destructor:
                foreach ( ParameterNode parameter in destructor.Parameters ) { yield return parameter; }
                yield return destructor.Body;
                yield break;
            case DevBlockDeclNode devDecl:
                foreach ( AstNode declaration in devDecl.Declarations ) { yield return declaration; }
                yield break;
            case BlockNode block:
                foreach ( AstNode statement in block.Statements ) { yield return statement; }
                yield break;
            case IfNode ifNode:
                yield return ifNode.Condition;
                yield return ifNode.Then;
                if ( ifNode.Else is not null ) { yield return ifNode.Else; }
                yield break;
            case WhileNode whileNode:
                yield return whileNode.Condition;
                yield return whileNode.Body;
                yield break;
            case DoWhileNode doWhile:
                yield return doWhile.Body;
                yield return doWhile.Condition;
                yield break;
            case ForNode forNode:
                if ( forNode.Initializer is not null ) { yield return forNode.Initializer; }
                if ( forNode.Condition is not null ) { yield return forNode.Condition; }
                if ( forNode.Increment is not null ) { yield return forNode.Increment; }
                yield return forNode.Body;
                yield break;
            case ForeachNode foreachNode:
                yield return foreachNode.Collection;
                yield return foreachNode.Body;
                yield break;
            case SwitchNode switchNode:
                yield return switchNode.Subject;
                foreach ( CaseGroupNode caseGroup in switchNode.Cases ) { yield return caseGroup; }
                yield break;
            case CaseGroupNode caseGroup:
                foreach ( ExprNode? label in caseGroup.Labels )
                {
                    if ( label is not null ) { yield return label; }
                }

                foreach ( AstNode statement in caseGroup.Statements ) { yield return statement; }
                yield break;
            case ReturnNode returnNode:
                if ( returnNode.Value is not null ) { yield return returnNode.Value; }
                yield break;
            case WaitNode wait:
                yield return wait.Duration;
                yield break;
            case ConstDeclNode constDecl:
                yield return constDecl.Value;
                yield break;
            case ExprStatementNode exprStatement:
                yield return exprStatement.Expression;
                yield break;
            case DevBlockStmtNode devStmt:
                foreach ( AstNode statement in devStmt.Statements ) { yield return statement; }
                yield break;
            case AssignmentNode assignment:
                yield return assignment.Target;
                yield return assignment.Value;
                yield break;
            case BinaryNode binary:
                yield return binary.Left;
                yield return binary.Right;
                yield break;
            case TernaryNode ternary:
                yield return ternary.Condition;
                yield return ternary.WhenTrue;
                yield return ternary.WhenFalse;
                yield break;
            case PrefixNode prefix:
                yield return prefix.Operand;
                yield break;
            case PostfixNode postfix:
                yield return postfix.Operand;
                yield break;
            case ParenNode paren:
                yield return paren.Inner;
                yield break;
            case VectorNode vector:
                yield return vector.X;
                yield return vector.Y;
                yield return vector.Z;
                yield break;
            case MemberNode member:
                yield return member.Object;
                yield break;
            case IndexNode index:
                yield return index.Object;
                yield return index.Index;
                yield break;
            case PointerDerefNode pointer:
                yield return pointer.Pointer;
                yield break;
            case CallNode call:
                if ( call.Target is not null ) { yield return call.Target; }
                yield return call.Callee;
                foreach ( ExprNode argument in call.Arguments ) { yield return argument; }
                yield break;
            case ArrowCallNode arrow:
                yield return arrow.Object;
                foreach ( ExprNode argument in arrow.Arguments ) { yield return argument; }
                yield break;
            case NewNode newNode:
                foreach ( ExprNode argument in newNode.Arguments ) { yield return argument; }
                yield break;
            default:
                yield break;
        }
    }
}
