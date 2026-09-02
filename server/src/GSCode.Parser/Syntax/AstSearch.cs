using System.Collections.Immutable;
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

    /// <summary>
    /// Direct structural children of a node (expression operands included).
    ///
    /// Returns a STRUCT enumerable rather than an <c>IEnumerable</c>, and every caller is a
    /// <c>foreach</c> that binds to it by shape, so the walk allocates nothing. It used to be a
    /// <c>yield return</c> iterator, which allocated one state machine per node VISITED — and this
    /// is walked once per rule, by fifteen lints plus the reference, hint and typing passes, over
    /// trees of a million nodes. Measured on bo3: a bare full-tree walk of every script cost
    /// 128–145 ms through the iterator against 35–47 ms without it, three times over, and a variant
    /// that short-circuits leaves before the type switch measured the same as the plain one — so the
    /// allocation was the cost and the thirty-case switch was not.
    /// </summary>
    public static ChildEnumerable ChildrenOf(AstNode node)
    {
        switch ( node )
        {
            case ScriptNode script:
                return ChildEnumerable.Of(script.Elements);
            case FunctionNode function:
                return ChildEnumerable.Of(function.Parameters.CastArray<AstNode>(), function.Body);
            case ParameterNode parameter:
                return ChildEnumerable.Of(parameter.DefaultValue);
            case ClassNode classNode:
                return ChildEnumerable.Of(classNode.Members);
            case ConstructorNode constructor:
                return ChildEnumerable.Of(constructor.Parameters.CastArray<AstNode>(), constructor.Body);
            case DestructorNode destructor:
                return ChildEnumerable.Of(destructor.Parameters.CastArray<AstNode>(), destructor.Body);
            case DevBlockDeclNode devDecl:
                return ChildEnumerable.Of(devDecl.Declarations);
            case BlockNode block:
                return ChildEnumerable.Of(block.Statements);
            case IfNode ifNode:
                return ChildEnumerable.Of(ifNode.Condition, ifNode.Then, ifNode.Else);
            case WhileNode whileNode:
                return ChildEnumerable.Of(whileNode.Condition, whileNode.Body);
            case DoWhileNode doWhile:
                return ChildEnumerable.Of(doWhile.Body, doWhile.Condition);
            case ForNode forNode:
                return ChildEnumerable.Of(forNode.Initializer, forNode.Condition, forNode.Increment, forNode.Body);
            case ForeachNode foreachNode:
                return ChildEnumerable.Of(foreachNode.Collection, foreachNode.Body);
            case SwitchNode switchNode:
                return ChildEnumerable.Of(switchNode.Subject, switchNode.Cases.CastArray<AstNode>());
            case CaseGroupNode caseGroup:
                return ChildEnumerable.OfCaseGroup(caseGroup.Labels, caseGroup.Statements);
            case ReturnNode returnNode:
                return ChildEnumerable.Of(returnNode.Value);
            case WaitNode wait:
                return ChildEnumerable.Of(wait.Duration);
            case ConstDeclNode constDecl:
                return ChildEnumerable.Of(constDecl.Value);
            case ExprStatementNode exprStatement:
                return ChildEnumerable.Of(exprStatement.Expression);
            case DevBlockStmtNode devStmt:
                return ChildEnumerable.Of(devStmt.Statements);
            case AssignmentNode assignment:
                return ChildEnumerable.Of(assignment.Target, assignment.Value);
            case BinaryNode binary:
                return ChildEnumerable.Of(binary.Left, binary.Right);
            case TernaryNode ternary:
                return ChildEnumerable.Of(ternary.Condition, ternary.WhenTrue, ternary.WhenFalse);
            case PrefixNode prefix:
                return ChildEnumerable.Of(prefix.Operand);
            case PostfixNode postfix:
                return ChildEnumerable.Of(postfix.Operand);
            case ParenNode paren:
                return ChildEnumerable.Of(paren.Inner);
            case VectorNode vector:
                return ChildEnumerable.Of(vector.X, vector.Y, vector.Z);
            case MemberNode member:
                return ChildEnumerable.Of(member.Object);
            case IndexNode index:
                return ChildEnumerable.Of(index.Object, index.Index);
            case PointerDerefNode pointer:
                return ChildEnumerable.Of(pointer.Pointer);
            case CallNode call:
                return ChildEnumerable.Of(call.Target, call.Callee, call.Arguments.CastArray<AstNode>());
            case ArrowCallNode arrow:
                return ChildEnumerable.Of(arrow.Object, arrow.Arguments.CastArray<AstNode>());
            case NewNode newNode:
                return ChildEnumerable.Of(newNode.Arguments.CastArray<AstNode>());
            default:
                return ChildEnumerable.Empty;
        }
    }
}

/// <summary>
/// One node's children, in the order the tree declares them, held without allocating.
///
/// Every shape in the AST is some of: a LEADING array (a block's statements, a function's
/// parameters), up to four NAMED children (a ternary's three operands, a for-loop's four parts),
/// a case group's LABELS — whose values are the children, and any of which may be absent — and a
/// TRAILING array (a call's arguments, a switch's case groups). The enumerator yields those four
/// groups in that order, skipping the absent ones, which reproduces the hand-written order the
/// <c>yield return</c> version had for every node type.
///
/// The arrays arrive through <c>CastArray</c>, which reinterprets the existing array rather than
/// copying it — <c>ImmutableArray&lt;T&gt;</c> is a struct, so an array of a derived node type is
/// not an array of <c>AstNode</c> without it.
/// </summary>
public readonly struct ChildEnumerable
{
    private readonly ImmutableArray<AstNode> _leading;
    private readonly AstNode? _first;
    private readonly AstNode? _second;
    private readonly AstNode? _third;
    private readonly AstNode? _fourth;
    private readonly ImmutableArray<CaseLabel> _labels;
    private readonly ImmutableArray<AstNode> _trailing;

    private ChildEnumerable(
        ImmutableArray<AstNode> leading,
        AstNode? first,
        AstNode? second,
        AstNode? third,
        AstNode? fourth,
        ImmutableArray<CaseLabel> labels,
        ImmutableArray<AstNode> trailing)
    {
        _leading = leading;
        _first = first;
        _second = second;
        _third = third;
        _fourth = fourth;
        _labels = labels;
        _trailing = trailing;
    }

    /// <summary>A leaf: an identifier, a literal, a directive, a `break`.</summary>
    public static ChildEnumerable Empty
    {
        get { return default; }
    }

    public static ChildEnumerable Of(ImmutableArray<AstNode> children)
    {
        return new ChildEnumerable(children, null, null, null, null, default, default);
    }

    public static ChildEnumerable Of(AstNode? first, AstNode? second = null, AstNode? third = null, AstNode? fourth = null)
    {
        return new ChildEnumerable(default, first, second, third, fourth, default, default);
    }

    /// <summary>A declaration's parameters or members, then its body.</summary>
    public static ChildEnumerable Of(ImmutableArray<AstNode> leading, AstNode body)
    {
        return new ChildEnumerable(leading, body, null, null, null, default, default);
    }

    /// <summary>A subject or receiver, then a list: a switch's cases, an arrow call's arguments.</summary>
    public static ChildEnumerable Of(AstNode subject, ImmutableArray<AstNode> trailing)
    {
        return new ChildEnumerable(default, subject, null, null, null, default, trailing);
    }

    /// <summary>A call: its optional target, its callee, then its arguments.</summary>
    public static ChildEnumerable Of(AstNode? target, AstNode callee, ImmutableArray<AstNode> trailing)
    {
        return new ChildEnumerable(default, target, callee, null, null, default, trailing);
    }

    /// <summary>The labels' values, then the statements they guard.</summary>
    public static ChildEnumerable OfCaseGroup(ImmutableArray<CaseLabel> labels, ImmutableArray<AstNode> statements)
    {
        return new ChildEnumerable(default, null, null, null, null, labels, statements);
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    /// <summary>
    /// Walks the four groups in order. A struct, and never boxed, because <c>foreach</c> binds to
    /// <c>GetEnumerator</c> by shape rather than through <c>IEnumerable</c> — which is the whole
    /// point of this type.
    /// </summary>
    public struct Enumerator
    {
        private readonly ChildEnumerable _children;
        private int _stage;
        private int _index;
        private AstNode? _current;

        internal Enumerator(ChildEnumerable children)
        {
            _children = children;
            _stage = 0;
            _index = 0;
            _current = null;
        }

        public AstNode Current
        {
            get { return _current!; }
        }

        public bool MoveNext()
        {
            while ( true )
            {
                switch ( _stage )
                {
                    case 0:
                        if ( !_children._leading.IsDefaultOrEmpty && _index < _children._leading.Length )
                        {
                            _current = _children._leading[_index++];
                            return true;
                        }

                        _stage = 1;
                        _index = 0;
                        continue;

                    case 1:
                        if ( _index >= 4 )
                        {
                            _stage = 2;
                            _index = 0;
                            continue;
                        }

                        // The named children, in declaration order, skipping the absent ones — an
                        // `if` with no `else`, a `for` with no initializer, a bare `return`.
                        AstNode? named = _index switch
                        {
                            0 => _children._first,
                            1 => _children._second,
                            2 => _children._third,
                            _ => _children._fourth,
                        };

                        _index++;
                        if ( named is not null )
                        {
                            _current = named;
                            return true;
                        }

                        continue;

                    case 2:
                        if ( !_children._labels.IsDefaultOrEmpty && _index < _children._labels.Length )
                        {
                            ExprNode? value = _children._labels[_index++].Value;
                            if ( value is not null )
                            {
                                _current = value;
                                return true;
                            }

                            continue;
                        }

                        _stage = 3;
                        _index = 0;
                        continue;

                    case 3:
                        if ( !_children._trailing.IsDefaultOrEmpty && _index < _children._trailing.Length )
                        {
                            _current = _children._trailing[_index++];
                            return true;
                        }

                        _stage = 4;
                        continue;

                    default:
                        return false;
                }
            }
        }
    }
}
