using GSCode.Core.Instrumentation;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

public sealed partial class Parser
{
    /// <summary>Full expression: assignment is the lowest level (right-associative).</summary>
    /// <remarks>
    /// One of the four points nesting is counted at; the others are <see cref="ParseTernary"/> (an
    /// arm, or a case label), <see cref="ParseUnary"/> (a prefix operand) and
    /// <see cref="ParseStatement"/>. Between them they cut every cycle in the call graph — the only
    /// back edge that avoids all four is <c>ParseBinary</c>'s own <c>ParseBinary(precedence + 1)</c>,
    /// which the ten entries of the precedence table already bound.
    /// </remarks>
    private ExprNode ParseExpression()
    {
#if GSCODE_INSTRUMENTATION
        // Only the OUTERMOST expression is timed. A scope at every level would nest inside itself —
        // `a + b * c` is several calls deep — and the report sums nested scopes, so the figure would
        // count the same microseconds once per level. One scope per expression entered from a
        // statement gives a share that can be subtracted from `parse.function` to leave the
        // statement and declaration structure behind.
        //
        // Guarded with #if rather than [Conditional] because a depth counter is not a call: the
        // increment would survive into a normal build, and this is the parser's hottest method.
        if ( _expressionDepth > 0 )
        {
            return ParseExpressionTracked();
        }

        PerfTracker.Begin("parse.expression");
        try
        {
            return ParseExpressionTracked();
        }
        finally
        {
            PerfTracker.End();
        }
    }

    private ExprNode ParseExpressionTracked()
    {
        _expressionDepth++;
        try
        {
            return ParseExpressionInner();
        }
        finally
        {
            _expressionDepth--;
        }
    }

    private ExprNode ParseExpressionInner()
    {
#endif
        if ( !EnterNesting() )
        {
            return AbandonNesting();
        }

        // Most expressions in a script are one token. An argument, an array index, the right-hand
        // side of a field assignment — `foo( a, 1, "x" )` is three expressions and not one of them
        // has an operator in it. Reaching ParsePrimary for those costs ParseExpressionCore,
        // ParseTernary, ParseTernaryCore, ParseBinary, ParseCallChain, ParseUnary, ParseUnaryCore
        // and ParsePostfix — eight calls that each look at one token, decide it is not theirs, and
        // descend — plus two more EnterNesting/ExitNesting pairs on the way down and a postfix loop,
        // a method-callee test, a precedence lookup, a `?` test and an assignment test on the way
        // back up.
        //
        // The nesting claim above is taken first and released either way, so the depth limit still
        // behaves exactly as it did: this is a shortcut through the descent, not around the guard.
        if ( TryParseLeafExpression(out ExprNode? leaf) )
        {
            ExitNesting();
            return leaf;
        }

        ExprNode expression = ParseExpressionCore();
        ExitNesting();
        return expression;
    }

    /// <summary>
    /// Parses a one-token expression directly, when the token AFTER it proves that every level of
    /// the descent would have been a pass-through.
    ///
    /// The follower set is the whole argument. Each of the four tokens below fails every test the
    /// skipped levels apply, and they were checked one level at a time rather than assumed:
    /// none is an assignment operator (<c>ParseExpressionCore</c>), none is <c>?</c>
    /// (<c>ParseTernaryCore</c>), none has a binary precedence (<c>ParseBinary</c>), none begins a
    /// method call — that needs <c>thread</c>, <c>childthread</c>, <c>call</c>, an identifier, or
    /// <c>[[</c> (<c>ParseCallChain</c>) — and none is a postfix operator, so <c>.</c>, <c>[</c>,
    /// <c>(</c>, <c>++</c>, <c>--</c> and <c>-&gt;</c> are all excluded
    /// (<c>ParsePostfixChainCore</c>). The token itself is one <c>ParsePrimary</c> builds from a
    /// single <c>Advance</c>, and the two lookahead forms it would otherwise check for —
    /// <c>name::name</c> and an inline <c>path\name</c> — both need a token these four are not.
    ///
    /// <c>:</c> is deliberately NOT in the set. A ternary's arms and a <c>case</c> label are parsed
    /// by <see cref="ParseTernary"/> rather than by this method, so admitting it would buy nothing
    /// and would put the shortcut a lookahead away from the one place a colon is structural.
    /// </summary>
    private bool TryParseLeafExpression([NotNullWhen(true)] out ExprNode? leaf)
    {
        leaf = null;

        switch ( Peek(1).Kind )
        {
            case TokenKind.Comma:
            case TokenKind.CloseParen:
            case TokenKind.CloseBracket:
            case TokenKind.Semicolon:
                break;
            default:
                return false;
        }

        // Exactly the single-Advance cases of ParsePrimary, split the way it splits them: the ones
        // that become a literal, and the ones that become a name.
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
                leaf = new LiteralNode(token.RootRange, token);
                return true;
            }
            case TokenKind.Identifier:
            case TokenKind.Vararg:
            case TokenKind.ThisThread:
            {
                PToken token = Advance();
                leaf = new IdentifierNode(token.RootRange, token);
                return true;
            }
            default:
                return false;
        }
    }

    private ExprNode ParseExpressionCore()
    {
        ExprNode left = ParseTernary();

        if ( TokenFacts.IsAssignmentOperator(Kind) )
        {
            TextRange operatorRange = Current.RootRange;
            TokenKind op = Advance().Kind;
            ExprNode value = ParseExpression();

            // `true = false;` parses cleanly — a literal IS an expression and `=` follows it — so
            // nothing objected here and the only complaint came from whatever mis-parsed after it,
            // which is how it surfaced as a bare "unexpected TOKEN_EQUALS". Assignment needs
            // somewhere to PUT the value, and a literal, a call result or an arithmetic result is
            // not somewhere.
            if ( !IsAssignableTarget(left) )
            {
                AddError(GscDiagnosticCode.InvalidAssignmentTarget, operatorRange, DescribeTarget(left));
            }

            return new AssignmentNode(SpanOf(left, value), left, op, value);
        }

        return left;
    }

    /// <summary>
    /// Whether a value can be stored INTO this expression: a variable, a field, an array element,
    /// or whatever a pointer dereference names. A parenthesised target is judged by its contents,
    /// since `( x ) = 1` stores into x exactly as `x = 1` does.
    /// </summary>
    private static bool IsAssignableTarget(ExprNode target)
    {
        return target switch
        {
            IdentifierNode => true,
            MemberNode => true,
            IndexNode => true,
            PointerDerefNode => true,
            ParenNode paren => IsAssignableTarget(paren.Inner),
            _ => false,
        };
    }

    /// <summary>What the invalid target IS, so the message names the mistake and not a token kind.</summary>
    private static string DescribeTarget(ExprNode target)
    {
        return target switch
        {
            LiteralNode literal => "'" + literal.Token.Text + "'",
            CallNode or ArrowCallNode => "a function call",
            BinaryNode => "an arithmetic result",
            TernaryNode => "a conditional result",
            VectorNode => "a vector literal",
            NewNode => "a new object",
            _ => "this expression",
        };
    }

    /// <summary>cond ? whenTrue : whenFalse — supported by the engine though absent from the PDF.</summary>
    private ExprNode ParseTernary()
    {
        if ( !EnterNesting() )
        {
            return AbandonNesting();
        }

        ExprNode expression = ParseTernaryCore();
        ExitNesting();
        return expression;
    }

    private ExprNode ParseTernaryCore()
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
        int levels = 0;

        while ( true )
        {
            int precedence = GetBinaryPrecedence(Kind);
            if ( precedence < minPrecedence )
            {
                break;
            }

            // The loop costs the parser nothing, but each pass wraps what came before in one more
            // BinaryNode, and every walker over the result recurses down that chain. `1 + 1 + …`
            // overflowed SymbolExtractor at ~1,430 terms, so the chain counts too.
            if ( !EnterNesting() )
            {
                StopAtNestingLimit();
                break;
            }

            levels++;
            TokenKind op = Advance().Kind;
            ExprNode right = ParseBinary(precedence + 1);
            left = new BinaryNode(SpanOf(left, right), left, op, right);
        }

        ExitNesting(levels);
        return left;
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

    // --- Method-notation call level: expr [thread] callee(args) ---

    /// <summary>
    /// Handles GSC's method-call notation: an expression followed by (optionally
    /// 'thread' and) a callable — e.g. player giveweapon(...), ent thread go(),
    /// level waittill("x"), target [[ptr]]->method().
    /// </summary>
    private ExprNode ParseCallChain()
    {
        ExprNode expression = ParseUnary();
        int levels = 0;

        // A call result IS a legal method object, and shipped code leans on it:
        // `guyPackets[i]["guy"] get_anim_ent() waittillmatch(msg, "end");` and
        // `GetEnt("monitor_06", "targetname") setclientflagasval(48);` are both BO1's own raw
        // scripts. So the chain loops.
        //
        // What it must NOT do is loop across a line break, because a second callee on the next line
        // is far more often a missing semicolon than a chain. CoD4's
        // animscripts\traverse\stairs_down.gsc is the case:
        //
        //     endnode = self getnegotiationendnode()      <- no semicolon
        //     assert( isdefined( endnode ) );
        //
        // Looping unconditionally read `assert(...)` as called ON the result of
        // getnegotiationendnode(), welded two statements into one and swallowed the missing
        // semicolon entirely; the report then landed two lines further down. The line break is the
        // only thing that separates the two readings, so the chain continues only while the next
        // callee sits on the line the previous call ended on.
        //
        // The FIRST call is not gated that way — it is the object's own statement either way, and
        // `self\n    thread foo();` is a formatting choice, not an omission.
        while ( StartsMethodCall() )
        {
            if ( !EnterNesting() )
            {
                StopAtNestingLimit();
                break;
            }

            levels++;

            // thread / childthread run the callee on a (child) thread; call invokes a function
            // pointer synchronously. All three are method-notation modifiers over the target.
            bool isThread = false;
            if ( Kind == TokenKind.Thread || Kind == TokenKind.ChildThread || Kind == TokenKind.Call )
            {
                isThread = Kind != TokenKind.Call;
                Advance();
            }

            // The postfix level runs per link, so a call RESULT can be indexed or member-accessed as
            // a temporary — `players[q] getplayerangles()[1]`, `ent getstruct().field`.
            expression = ParsePostfixChain(ParseCallCore(expression, isThread));

            if ( !ContinuesPreviousLine() )
            {
                break;
            }
        }

        ExitNesting(levels);
        return expression;
    }

    /// <summary>True when a method-notation call begins here: a modifier keyword, or a bare callee.</summary>
    private bool StartsMethodCall()
    {
        return Kind == TokenKind.Thread
            || Kind == TokenKind.ChildThread
            || Kind == TokenKind.Call
            || IsMethodCalleeAhead();
    }

    /// <summary>
    /// The field name after <c>.</c>. A keyword is a perfectly good field name — scripts really do
    /// write <c>self.wait</c>, <c>spawner.Wait</c>, <c>ent.size</c> — and nothing is ambiguous in
    /// member position, so a keyword token is accepted here and used as the name. Which words this
    /// covers is per-dialect, since the keyword set is; accepting them all means a field named after
    /// a keyword works in every game.
    /// </summary>
    private PToken ExpectFieldName()
    {
        if ( TokenFacts.IsKeyword(Kind) )
        {
            return Advance();
        }

        return Expect(TokenKind.Identifier, "field name");
    }

    /// <summary>True when the tokens ahead form a callee for method notation.</summary>
    private bool IsMethodCalleeAhead()
    {
        // Keyword callables (waittill, notify, ...) always read as method calls here.
        if ( IsCallableKeyword(Kind) )
        {
            return true;
        }

        // maps\mp\_utility::foo( — an Infinity Ward path-qualified method callee (gated dialect).
        if ( StartsInlinePath() )
        {
            return IsInlinePathCallAhead();
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

    /// <summary>
    /// True when a path-qualified reference begins here: an identifier joined by a backslash,
    /// e.g. maps\mp\_utility::foo. Only the Infinity Ward dialects have it (BO3 qualifies with
    /// ns::name), so it costs BO3 nothing — the flag is off and the check short-circuits.
    /// </summary>
    private bool StartsInlinePath()
    {
        return _profile.HasInlinePathCalls
            && Kind == TokenKind.Identifier
            && Peek(1).Kind == TokenKind.Backslash;
    }

    /// <summary>
    /// Given a path start, looks past the backslash path for <c>:: name (</c> — the shape of a
    /// path-qualified CALL, as opposed to a bare pointer. Assumes <see cref="StartsInlinePath"/>.
    /// </summary>
    private bool IsInlinePathCallAhead()
    {
        int offset = 1;
        while ( Peek(offset).Kind == TokenKind.Backslash && Peek(offset + 1).Kind == TokenKind.Identifier )
        {
            offset += 2;
        }

        return Peek(offset).Kind == TokenKind.ScopeResolution
            && Peek(offset + 1).Kind == TokenKind.Identifier
            && Peek(offset + 2).Kind == TokenKind.OpenParen;
    }

    /// <summary>
    /// Parses maps\mp\_utility::foo — the backslash path, then :: and the function name. The caller
    /// decides what follows: an argument list makes it a call, its absence a function pointer.
    /// </summary>
    private PathQualifiedNode ParsePathQualified()
    {
        PToken start = Current;
        System.Text.StringBuilder path = new();

        // The path is identifiers joined by backslashes; it ends at the :: qualifier.
        while ( Kind == TokenKind.Identifier || Kind == TokenKind.Backslash )
        {
            path.Append(Current.Text);
            Advance();
        }

        TextRange pathRange = new(start.RootRange.Start, _tokens[Math.Max(0, _index - 1)].RootRange.End);

        Expect(TokenKind.ScopeResolution, "::");
        PToken nameToken = Expect(TokenKind.Identifier, "function name");

        return new PathQualifiedNode(RangeFrom(start), path.ToString(), pathRange, nameToken);
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
        if ( StartsInlinePath() )
        {
            callee = ParsePathQualified();
        }
        else if ( Kind == TokenKind.Identifier && Peek(1).Kind == TokenKind.ScopeResolution )
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
            or TokenKind.VectorScale
            or TokenKind.ProfileStart
            or TokenKind.ProfileStop
            or TokenKind.Assert
            or TokenKind.AssertMsg;
    }

    // --- Unary / postfix / primary ---

    private ExprNode ParseUnary()
    {
        if ( !EnterNesting() )
        {
            return AbandonNesting();
        }

        ExprNode expression = ParseUnaryCore();
        ExitNesting();
        return expression;
    }

    private ExprNode ParseUnaryCore()
    {
        switch ( Kind )
        {
            case TokenKind.Thread:
            case TokenKind.ChildThread:
            {
                // thread foo() / childthread foo() with no target.
                Advance();
                return ParseCallCore(target: null, isThread: true);
            }
            case TokenKind.Call:
            {
                // call [[ ptr ]]( … ) with no target — a synchronous function-pointer call.
                Advance();
                return ParseCallCore(target: null, isThread: false);
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
                PToken op = Advance();

                // &"..." is a localized string, not address-of. The lexer folds the ADJACENT
                // form into one token, so reaching here means the two arrived apart: written
                // with a space, or the string supplied by a macro. Both are still istrings —
                // `&` only means address-of in front of a function name.
                if ( Kind == TokenKind.String )
                {
                    return ParseSplitLocalizedString(op);
                }

                // Function address-of: &name or &ns::name.
                ExprNode reference = ParseFunctionReference();
                return new PrefixNode(new TextRange(op.RootRange.Start, reference.Range.End), TokenKind.Ampersand, reference);
            }
            default:
                return ParsePostfix();
        }
    }

    /// <summary>
    /// Folds an `&amp;` and a following string into the localized-string literal the lexer would
    /// have produced had they been written adjacently.
    ///
    /// The token is rebuilt rather than wrapped so that everything downstream treats it exactly
    /// like a lexed istring: extraction strips the leading `&amp;` before unquoting, so the text
    /// must carry it, and the flow typer keys off the LocalizedString kind. Provenance follows
    /// the STRING, not the ampersand — when the string came from a macro body the literal
    /// belongs to that `#define`, and taking the ampersand's provenance would attribute it to
    /// the call site instead.
    /// </summary>
    private ExprNode ParseSplitLocalizedString(PToken ampersand)
    {
        PToken text = Advance();

        // Spanning the two only makes sense when they really sit together in one file.
        bool sameFile = ampersand.Provenance.SourceFile == text.Provenance.SourceFile
            && ampersand.Provenance.RootSite is null
            && text.Provenance.RootSite is null;

        TextRange range = sameFile
            ? new TextRange(ampersand.Range.Start, text.Range.End)
            : text.Range;

        PToken folded = new(TokenKind.LocalizedString, "&" + text.Text, range, text.Provenance);
        return new LiteralNode(folded.RootRange, folded);
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
        int levels = 0;
        ExprNode result = ParsePostfixChainCore(expression, ref levels);
        ExitNesting(levels);
        return result;
    }

    private ExprNode ParsePostfixChainCore(ExprNode expression, ref int levels)
    {
        while ( true )
        {
            // Claimed per pass rather than per case, because every case below that continues the
            // loop wraps `expression` in one more node — `a.b.c.d`, `a[0][0][0]` — and walkers
            // recurse down that chain exactly as they do a binary one. The claim the `default`
            // branch does not use is released with the rest.
            if ( !EnterNesting() )
            {
                StopAtNestingLimit();
                return expression;
            }

            levels++;

            switch ( Kind )
            {
                case TokenKind.Dot:
                {
                    Advance();
                    PToken nameToken = ExpectFieldName();
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
                case TokenKind.OpenParen when expression is IdentifierNode or QualifiedNode or PointerDerefNode or PathQualifiedNode:
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
        // maps\mp\_utility::foo — a leading path-qualified name (call or pointer). The postfix
        // level turns a following ( into a call; without one it stands as a function pointer.
        if ( StartsInlinePath() )
        {
            return ParsePathQualified();
        }

        // ::foo — the same reference with no path: an Infinity Ward LOCAL function pointer/call.
        // Modelled as a PathQualifiedNode with an empty path. BO3 requires a namespace before ::,
        // so this only fires on a dialect that has the path form.
        if ( _profile.HasInlinePathCalls && Kind == TokenKind.ScopeResolution )
        {
            PToken scope = Advance();
            PToken localName = Expect(TokenKind.Identifier, "function name");
            return new PathQualifiedNode(RangeFrom(scope), string.Empty, scope.RootRange, localName);
        }

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
            // The parameter pack reads as a value — indexed, counted, iterated — so it becomes an
            // IdentifierNode like any other name. Being a keyword only changes how it COLOURS and
            // that it cannot be a declaration target; every expression rule below treats it as the
            // array it is.
            case TokenKind.Vararg:
            {
                PToken pack = Advance();
                return new IdentifierNode(pack.RootRange, pack);
            }
            // The running thread reads as a value too — stored on a field, passed on, compared — so it
            // takes the same shape. It is never a call modifier the way `thread`/`childthread` are.
            case TokenKind.ThisThread:
            {
                PToken running = Advance();
                return new IdentifierNode(running.RootRange, running);
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
    private bool CanStartExpression(TokenKind kind)
    {
        if ( IsCallableKeyword(kind) )
        {
            return true;
        }

        // ::foo — a leading local function pointer/call, only in the path-call dialects. Gated so
        // BO3 still rejects a stray :: at the dispatcher (unchanged recovery).
        if ( kind == TokenKind.ScopeResolution && _profile.HasInlinePathCalls )
        {
            return true;
        }

        // Vararg and ThisThread are keywords but read as values, so either can open a statement the
        // same way a name can — `vararg[ 0 ] = x;`. Leaving the pack out made a statement starting
        // with it unrecognisable to recovery, which no stock script would have revealed: they only
        // ever read it mid-expression.
        return kind is TokenKind.Identifier
            or TokenKind.Vararg
            or TokenKind.ThisThread
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
            or TokenKind.ChildThread
            or TokenKind.Call
            or TokenKind.New;
    }

    private static TextRange SpanOf(ExprNode first, ExprNode last)
    {
        return new TextRange(first.Range.Start, last.Range.End);
    }
}
