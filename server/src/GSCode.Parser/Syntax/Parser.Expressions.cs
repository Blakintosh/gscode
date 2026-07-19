using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

public sealed partial class Parser
{
    /// <summary>Full expression: assignment is the lowest level (right-associative).</summary>
    private ExprNode ParseExpression()
    {
        ExprNode left = ParseTernary();

        if ( IsAssignmentOperator(Kind) )
        {
            TokenKind op = Advance().Kind;
            ExprNode value = ParseExpression();
            return new AssignmentNode(SpanOf(left, value), left, op, value);
        }

        return left;
    }

    /// <summary>cond ? whenTrue : whenFalse — supported by the engine though absent from the PDF.</summary>
    private ExprNode ParseTernary()
    {
        ExprNode condition = ParseBinary(1);

        if ( !Match(TokenKind.QuestionMark) )
        {
            return condition;
        }

        ExprNode whenTrue = ParseTernary();
        Expect(TokenKind.Colon, ":");
        ExprNode whenFalse = ParseTernary();

        return new TernaryNode(SpanOf(condition, whenFalse), condition, whenTrue, whenFalse);
    }

    /// <summary>Precedence climbing over the binary operator table (left-associative).</summary>
    private ExprNode ParseBinary(int minPrecedence)
    {
        ExprNode left = ParseCallChain();

        while ( true )
        {
            int precedence = GetBinaryPrecedence(Kind);
            if ( precedence < minPrecedence )
            {
                return left;
            }

            TokenKind op = Advance().Kind;
            ExprNode right = ParseBinary(precedence + 1);
            left = new BinaryNode(SpanOf(left, right), left, op, right);
        }
    }

    private static int GetBinaryPrecedence(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.LogicalOr: return 1;
            case TokenKind.LogicalAnd: return 2;
            case TokenKind.Pipe: return 3;
            case TokenKind.Caret: return 4;
            case TokenKind.Ampersand: return 5;
            case TokenKind.EqualsEquals:
            case TokenKind.NotEquals:
            case TokenKind.StrictEquals:
            case TokenKind.StrictNotEquals: return 6;
            case TokenKind.LessThan:
            case TokenKind.LessThanEquals:
            case TokenKind.GreaterThan:
            case TokenKind.GreaterThanEquals: return 7;
            case TokenKind.ShiftLeft:
            case TokenKind.ShiftRight: return 8;
            case TokenKind.Plus:
            case TokenKind.Minus: return 9;
            case TokenKind.Star:
            case TokenKind.Slash:
            case TokenKind.Percent: return 10;
            default: return 0;
        }
    }

    private static bool IsAssignmentOperator(TokenKind kind)
    {
        return kind is TokenKind.Assign
            or TokenKind.PlusAssign
            or TokenKind.MinusAssign
            or TokenKind.StarAssign
            or TokenKind.SlashAssign
            or TokenKind.PercentAssign
            or TokenKind.AmpersandAssign
            or TokenKind.PipeAssign
            or TokenKind.CaretAssign
            or TokenKind.ShiftLeftAssign
            or TokenKind.ShiftRightAssign;
    }

    // --- Method-notation call level: expr [thread] callee(args) ---

    /// <summary>
    /// Handles GSC's method-call notation: an expression followed by (optionally
    /// 'thread' and) a callable — e.g. player giveweapon(...), ent thread go(),
    /// level waittill("x"), target [[ptr]]->method().
    /// </summary>
    private ExprNode ParseCallChain()
    {
        ExprNode expression = ParseUnary();

        while ( true )
        {
            if ( Kind == TokenKind.Thread )
            {
                Advance();
                expression = ParseCallCore(expression, isThread: true);
                expression = ParsePostfixChain(expression);
                continue;
            }

            if ( IsMethodCalleeAhead() )
            {
                expression = ParseCallCore(expression, isThread: false);
                // A call result can be indexed or member-accessed directly (used as a temporary),
                // e.g. players[q] getplayerangles()[1] or ent getstruct().field.
                expression = ParsePostfixChain(expression);
                continue;
            }

            return expression;
        }
    }

    /// <summary>True when the tokens ahead form a callee for method notation.</summary>
    private bool IsMethodCalleeAhead()
    {
        // Keyword callables (waittill, notify, ...) always read as method calls here.
        if ( IsCallableKeyword(Kind) )
        {
            return true;
        }

        // identifier( or identifier::identifier( — require the paren so plain
        // juxtaposed identifiers (an error) don't parse as calls.
        if ( Kind == TokenKind.Identifier )
        {
            if ( Peek(1).Kind == TokenKind.OpenParen )
            {
                return true;
            }

            if ( Peek(1).Kind == TokenKind.ScopeResolution
                && Peek(2).Kind == TokenKind.Identifier
                && Peek(3).Kind == TokenKind.OpenParen )
            {
                return true;
            }

            return false;
        }

        // [[ptr]](...) or [[obj]]->method(...)
        return IsPointerDerefAhead();
    }

    private bool IsPointerDerefAhead()
    {
        // Two consecutive '[' in the trivia-free stream are always a pointer deref ([[ptr]]):
        // a nested index like a[b[1]] has its operand between the brackets, and non-empty
        // '[...]' array literals don't exist in the language. Spacing is therefore irrelevant,
        // so `[ [ ptr ] ]` reads the same as `[[ptr]]`.
        return Kind == TokenKind.OpenBracket
            && Peek(1).Kind == TokenKind.OpenBracket;
    }

    /// <summary>Parses callee + argument list into a call node (target/thread supplied by the caller).</summary>
    private ExprNode ParseCallCore(ExprNode? target, bool isThread)
    {
        PToken start = Current;

        if ( IsPointerDerefAhead() )
        {
            PointerDerefNode pointer = ParsePointerDeref();

            if ( Match(TokenKind.Arrow) )
            {
                PToken methodToken = Expect(TokenKind.Identifier, "method name");
                ImmutableArray<ExprNode> arrowArguments = ParseArgumentList();
                return new ArrowCallNode(RangeFrom(start), pointer, methodToken, arrowArguments);
            }

            ImmutableArray<ExprNode> pointerArguments = ParseArgumentList();
            return new CallNode(RangeFrom(start), target, isThread, pointer, pointerArguments);
        }

        ExprNode callee;
        if ( Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.ScopeResolution )
        {
            PToken namespaceToken = Advance();
            Advance();
            PToken nameToken = Expect(TokenKind.Identifier, "function name");
            callee = new QualifiedNode(RangeFrom(namespaceToken), namespaceToken, nameToken);
        }
        else if ( Kind == TokenKind.Identifier || IsCallableKeyword(Kind) )
        {
            PToken calleeToken = Advance();
            callee = new IdentifierNode(calleeToken.RootRange, calleeToken);
        }
        else
        {
            AddError(GscDiagnosticCode.ExpectedExpression, Current.RootRange, DescribeCurrent());
            callee = new ErrorNode(Current.RootRange);
        }

        ImmutableArray<ExprNode> arguments = ParseArgumentList();
        return new CallNode(RangeFrom(start), target, isThread, callee, arguments);
    }

    private ImmutableArray<ExprNode> ParseArgumentList()
    {
        ImmutableArray<ExprNode>.Builder arguments = ImmutableArray.CreateBuilder<ExprNode>();
        Expect(TokenKind.OpenParen, "(");

        while ( Kind != TokenKind.CloseParen && Kind != TokenKind.EndOfFile && Kind != TokenKind.Semicolon )
        {
            arguments.Add(ParseExpression());

            if ( !Match(TokenKind.Comma) )
            {
                break;
            }
        }

        Expect(TokenKind.CloseParen, ")");
        return arguments.ToImmutable();
    }

    /// <summary>Keywords that read like builtin calls: waittill family, notify/endon, asserts, intrinsics.</summary>
    private static bool IsCallableKeyword(TokenKind kind)
    {
        return kind is TokenKind.WaitTill
            or TokenKind.WaitTillMatch
            or TokenKind.Notify
            or TokenKind.Endon
            or TokenKind.IsDefined
            or TokenKind.GetTime
            or TokenKind.VectorScale
            or TokenKind.ProfileStart
            or TokenKind.ProfileStop
            or TokenKind.Assert
            or TokenKind.AssertMsg;
    }

    // --- Unary / postfix / primary ---

    private ExprNode ParseUnary()
    {
        switch ( Kind )
        {
            case TokenKind.Thread:
            {
                // thread foo() with no target.
                Advance();
                return ParseCallCore(target: null, isThread: true);
            }
            case TokenKind.Bang:
            case TokenKind.Tilde:
            case TokenKind.Minus:
            {
                PToken op = Advance();
                ExprNode operand = ParseUnary();
                return new PrefixNode(new TextRange(op.RootRange.Start, operand.Range.End), op.Kind, operand);
            }
            case TokenKind.Ampersand:
            {
                // Function address-of: &name or &ns::name.
                PToken op = Advance();
                ExprNode reference = ParseFunctionReference();
                return new PrefixNode(new TextRange(op.RootRange.Start, reference.Range.End), TokenKind.Ampersand, reference);
            }
            default:
                return ParsePostfix();
        }
    }

    private ExprNode ParseFunctionReference()
    {
        if ( Kind != TokenKind.Identifier )
        {
            AddError(GscDiagnosticCode.ExpectedExpression, Current.RootRange, DescribeCurrent());
            return new ErrorNode(Current.RootRange);
        }

        PToken first = Advance();
        if ( Kind == TokenKind.ScopeResolution )
        {
            Advance();
            PToken nameToken = Expect(TokenKind.Identifier, "function name");
            return new QualifiedNode(RangeFrom(first), first, nameToken);
        }

        return new IdentifierNode(first.RootRange, first);
    }

    private ExprNode ParsePostfix()
    {
        return ParsePostfixChain(ParsePrimary());
    }

    /// <summary>
    /// Applies postfix operators (.field, [index], call-of-name, ++/--, [[ptr]]->method) to an
    /// already-parsed expression. Shared by the primary level and the method-call level so a
    /// call result can be indexed or member-accessed directly.
    /// </summary>
    private ExprNode ParsePostfixChain(ExprNode expression)
    {
        while ( true )
        {
            switch ( Kind )
            {
                case TokenKind.Dot:
                {
                    Advance();
                    PToken nameToken = Expect(TokenKind.Identifier, "field name");
                    expression = new MemberNode(new TextRange(expression.Range.Start, nameToken.RootRange.End), expression, nameToken);
                    continue;
                }
                case TokenKind.OpenBracket when !IsPointerDerefAhead():
                {
                    // Single '[' is indexing; an adjacent '[[' belongs to method-notation
                    // pointer calls (ent [[ptr]](...)), which the call-chain level owns.
                    Advance();
                    ExprNode index = ParseExpression();
                    PToken close = Expect(TokenKind.CloseBracket, "]");
                    expression = new IndexNode(new TextRange(expression.Range.Start, close.RootRange.End), expression, index);
                    continue;
                }
                case TokenKind.OpenParen when expression is IdentifierNode or QualifiedNode or PointerDerefNode:
                {
                    ImmutableArray<ExprNode> arguments = ParseArgumentList();
                    TextRange range = new(expression.Range.Start, _tokens[Math.Max(0, _index - 1)].RootRange.End);
                    expression = new CallNode(range, Target: null, IsThread: false, expression, arguments);
                    continue;
                }
                case TokenKind.PlusPlus:
                case TokenKind.MinusMinus:
                {
                    PToken op = Advance();
                    expression = new PostfixNode(new TextRange(expression.Range.Start, op.RootRange.End), expression, op.Kind);
                    continue;
                }
                case TokenKind.Arrow when expression is PointerDerefNode pointer:
                {
                    Advance();
                    PToken methodToken = Expect(TokenKind.Identifier, "method name");
                    ImmutableArray<ExprNode> arguments = ParseArgumentList();
                    TextRange range = new(expression.Range.Start, _tokens[Math.Max(0, _index - 1)].RootRange.End);
                    expression = new ArrowCallNode(range, pointer, methodToken, arguments);
                    continue;
                }
                default:
                    return expression;
            }
        }
    }

    private PointerDerefNode ParsePointerDeref()
    {
        PToken firstBracket = Advance();
        Advance();

        ExprNode pointer = ParseExpression();

        Expect(TokenKind.CloseBracket, "]");
        Expect(TokenKind.CloseBracket, "]");

        return new PointerDerefNode(RangeFrom(firstBracket), pointer);
    }

    private ExprNode ParsePrimary()
    {
        switch ( Kind )
        {
            case TokenKind.Integer:
            case TokenKind.Float:
            case TokenKind.Hex:
            case TokenKind.String:
            case TokenKind.LocalizedString:
            case TokenKind.HashString:
            case TokenKind.AnimReference:
            case TokenKind.True:
            case TokenKind.False:
            case TokenKind.Undefined:
            case TokenKind.AnimTreeDirective:
            {
                PToken token = Advance();
                return new LiteralNode(token.RootRange, token);
            }
            case TokenKind.Identifier:
            {
                PToken first = Advance();
                if ( Kind == TokenKind.ScopeResolution )
                {
                    Advance();
                    PToken nameToken = Expect(TokenKind.Identifier, "name");
                    return new QualifiedNode(RangeFrom(first), first, nameToken);
                }

                return new IdentifierNode(first.RootRange, first);
            }
            case TokenKind.OpenParen:
            {
                PToken open = Advance();
                ExprNode first = ParseExpression();

                if ( Match(TokenKind.Comma) )
                {
                    ExprNode second = ParseExpression();
                    Expect(TokenKind.Comma, ",");
                    ExprNode third = ParseExpression();
                    Expect(TokenKind.CloseParen, ")");
                    return new VectorNode(RangeFrom(open), first, second, third);
                }

                Expect(TokenKind.CloseParen, ")");
                return new ParenNode(RangeFrom(open), first);
            }
            case TokenKind.OpenBracket:
            {
                if ( Peek(1).Kind == TokenKind.CloseBracket )
                {
                    PToken open = Advance();
                    Advance();
                    return new ArrayLiteralNode(RangeFrom(open));
                }

                if ( IsPointerDerefAhead() )
                {
                    return ParsePointerDeref();
                }

                AddError(GscDiagnosticCode.ExpectedExpression, Current.RootRange, DescribeCurrent());
                return MakeErrorAndAdvance();
            }
            case TokenKind.New:
            {
                PToken keyword = Advance();
                PToken classToken = Expect(TokenKind.Identifier, "class name");
                ImmutableArray<ExprNode> arguments = ParseArgumentList();
                return new NewNode(RangeFrom(keyword), classToken, arguments);
            }
            default:
            {
                if ( IsCallableKeyword(Kind) )
                {
                    PToken keywordToken = Advance();
                    return new IdentifierNode(keywordToken.RootRange, keywordToken);
                }

                AddError(GscDiagnosticCode.ExpectedExpression, Current.RootRange, DescribeCurrent());
                return MakeErrorAndAdvance();
            }
        }
    }

    /// <summary>Error placeholder that guarantees forward progress unless at a closer.</summary>
    private ExprNode MakeErrorAndAdvance()
    {
        TextRange range = Current.RootRange;

        bool atCloser = Kind is TokenKind.CloseParen
            or TokenKind.CloseBracket
            or TokenKind.CloseBrace
            or TokenKind.Semicolon
            or TokenKind.Comma
            or TokenKind.DevBlockClose
            or TokenKind.EndOfFile;

        if ( !atCloser )
        {
            Advance();
        }

        return new ErrorNode(range);
    }

    /// <summary>True when a token can begin an expression (drives statement-level recovery).</summary>
    private static bool CanStartExpression(TokenKind kind)
    {
        if ( IsCallableKeyword(kind) )
        {
            return true;
        }

        return kind is TokenKind.Identifier
            or TokenKind.Integer
            or TokenKind.Float
            or TokenKind.Hex
            or TokenKind.String
            or TokenKind.LocalizedString
            or TokenKind.HashString
            or TokenKind.AnimReference
            or TokenKind.True
            or TokenKind.False
            or TokenKind.Undefined
            or TokenKind.AnimTreeDirective
            or TokenKind.OpenParen
            or TokenKind.OpenBracket
            or TokenKind.Bang
            or TokenKind.Tilde
            or TokenKind.Minus
            or TokenKind.Ampersand
            or TokenKind.Thread
            or TokenKind.New;
    }

    private static TextRange SpanOf(ExprNode first, ExprNode last)
    {
        return new TextRange(first.Range.Start, last.Range.End);
    }
}
