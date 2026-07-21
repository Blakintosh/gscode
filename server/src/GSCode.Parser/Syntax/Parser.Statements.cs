using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

public sealed partial class Parser
{
    /// <summary>{ statements } — the workhorse body parser.</summary>
    private BlockNode ParseBlock()
    {
        PToken open = Expect(TokenKind.OpenBrace, "{");
        ImmutableArray<AstNode>.Builder statements = ImmutableArray.CreateBuilder<AstNode>();

        while ( Kind != TokenKind.CloseBrace && Kind != TokenKind.EndOfFile )
        {
            statements.Add(ParseStatement());
        }

        if ( !Match(TokenKind.CloseBrace) )
        {
            AddError(GscDiagnosticCode.UnterminatedBlock, open.RootRange);
        }

        return new BlockNode(RangeFrom(open), statements.ToImmutable());
    }

    private AstNode ParseStatement()
    {
        switch ( Kind )
        {
            case TokenKind.OpenBrace:
                return ParseBlock();
            case TokenKind.If:
                return ParseIf();
            case TokenKind.While:
                return ParseWhile();
            case TokenKind.Do:
                return ParseDoWhile();
            case TokenKind.For:
                return ParseFor();
            case TokenKind.Foreach:
                return ParseForeach();
            case TokenKind.Switch:
                return ParseSwitch();
            case TokenKind.Return:
                return ParseReturn();
            case TokenKind.Break:
            {
                PToken keyword = Advance();
                Expect(TokenKind.Semicolon, ";");
                return new BreakNode(RangeFrom(keyword));
            }
            case TokenKind.Continue:
            {
                PToken keyword = Advance();
                Expect(TokenKind.Semicolon, ";");
                return new ContinueNode(RangeFrom(keyword));
            }
            case TokenKind.Wait:
            case TokenKind.WaitRealTime:
            {
                PToken keyword = Advance();
                ExprNode duration = ParseExpression();
                Expect(TokenKind.Semicolon, ";");
                return new WaitNode(RangeFrom(keyword), duration, keyword.Kind == TokenKind.WaitRealTime);
            }
            case TokenKind.WaitTillFrameEnd:
            {
                PToken keyword = Advance();
                Expect(TokenKind.Semicolon, ";");
                return new WaitTillFrameEndNode(RangeFrom(keyword));
            }
            case TokenKind.Const:
            {
                PToken keyword = Advance();
                PToken nameToken = Expect(TokenKind.Identifier, "constant name");
                Expect(TokenKind.Assign, "=");
                ExprNode value = ParseExpression();
                Expect(TokenKind.Semicolon, ";");
                return new ConstDeclNode(RangeFrom(keyword), nameToken, value);
            }
            case TokenKind.Semicolon:
            {
                PToken semicolon = Advance();
                return new EmptyStatementNode(semicolon.RootRange);
            }
            case TokenKind.DevBlockOpen:
                return ParseDevBlockStatements();
            case TokenKind.DevBlockClose:
            {
                // Same tolerance as at declaration level: a `#/` with nothing open is skipped
                // rather than reported, since the engine accepts it and the surrounding
                // statements are unaffected either way.
                PToken stray = Advance();
                return new EmptyStatementNode(stray.RootRange);
            }
            default:
                return ParseExpressionStatement();
        }
    }

    private AstNode ParseExpressionStatement()
    {
        PToken start = Current;

        if ( !CanStartExpression(Kind) )
        {
            AddError(GscDiagnosticCode.ExpectedStatement, Current.RootRange, DescribeCurrent());
            RecoverToStatement();
            return new ErrorNode(RangeFrom(start));
        }

        ExprNode expression = ParseExpression();

        if ( !Match(TokenKind.Semicolon) )
        {
            AddError(GscDiagnosticCode.ExpectedToken, Current.RootRange, ";", DescribeCurrent());
            RecoverToStatement();
        }

        return new ExprStatementNode(RangeFrom(start), expression);
    }

    /// <summary>
    /// Skips to the next statement boundary; consumes a found ';'. The sync check runs
    /// BEFORE any advance (a statement keyword like 'return' must survive recovery);
    /// callers have always consumed at least one token first, so progress holds.
    /// </summary>
    private void RecoverToStatement()
    {
        while ( Kind != TokenKind.EndOfFile )
        {
            if ( Kind == TokenKind.Semicolon )
            {
                Advance();
                return;
            }

            bool atSyncPoint = Kind is TokenKind.CloseBrace
                or TokenKind.DevBlockClose
                or TokenKind.If
                or TokenKind.While
                or TokenKind.For
                or TokenKind.Foreach
                or TokenKind.Switch
                or TokenKind.Return
                or TokenKind.Break
                or TokenKind.Continue
                or TokenKind.Wait
                or TokenKind.Function
                or TokenKind.Class;

            if ( atSyncPoint )
            {
                return;
            }

            Advance();
        }
    }

    /// <summary>Skips (at least one token) to the next case/default/closing brace inside a switch.</summary>
    private void RecoverToCaseLabel()
    {
        Advance();

        while ( Kind != TokenKind.EndOfFile )
        {
            if ( Kind is TokenKind.Case or TokenKind.Default or TokenKind.CloseBrace )
            {
                return;
            }

            Advance();
        }
    }

    private IfNode ParseIf()
    {
        PToken keyword = Advance();
        Expect(TokenKind.OpenParen, "(");
        ExprNode condition = ParseExpression();
        Expect(TokenKind.CloseParen, ")");

        AstNode thenBranch = ParseStatement();

        AstNode? elseBranch = null;
        if ( Match(TokenKind.Else) )
        {
            elseBranch = ParseStatement();
        }

        return new IfNode(RangeFrom(keyword), condition, thenBranch, elseBranch);
    }

    private WhileNode ParseWhile()
    {
        PToken keyword = Advance();
        Expect(TokenKind.OpenParen, "(");
        ExprNode condition = ParseExpression();
        Expect(TokenKind.CloseParen, ")");
        AstNode body = ParseStatement();

        return new WhileNode(RangeFrom(keyword), condition, body);
    }

    private DoWhileNode ParseDoWhile()
    {
        PToken keyword = Advance();
        AstNode body = ParseStatement();
        Expect(TokenKind.While, "while");
        Expect(TokenKind.OpenParen, "(");
        ExprNode condition = ParseExpression();
        Expect(TokenKind.CloseParen, ")");
        Expect(TokenKind.Semicolon, ";");

        return new DoWhileNode(RangeFrom(keyword), body, condition);
    }

    private ForNode ParseFor()
    {
        PToken keyword = Advance();
        Expect(TokenKind.OpenParen, "(");

        AstNode? initializer = null;
        if ( Kind != TokenKind.Semicolon )
        {
            ExprNode initExpression = ParseExpression();
            initializer = new ExprStatementNode(initExpression.Range, initExpression);
        }

        Expect(TokenKind.Semicolon, ";");

        ExprNode? condition = null;
        if ( Kind != TokenKind.Semicolon )
        {
            condition = ParseExpression();
        }

        Expect(TokenKind.Semicolon, ";");

        AstNode? increment = null;
        if ( Kind != TokenKind.CloseParen )
        {
            ExprNode incrementExpression = ParseExpression();
            increment = new ExprStatementNode(incrementExpression.Range, incrementExpression);
        }

        Expect(TokenKind.CloseParen, ")");
        AstNode body = ParseStatement();

        return new ForNode(RangeFrom(keyword), initializer, condition, increment, body);
    }

    private ForeachNode ParseForeach()
    {
        PToken keyword = Advance();
        Expect(TokenKind.OpenParen, "(");

        PToken firstVariable = Expect(TokenKind.Identifier, "loop variable");

        PToken? keyToken = null;
        PToken valueToken = firstVariable;
        if ( Match(TokenKind.Comma) )
        {
            keyToken = firstVariable;
            valueToken = Expect(TokenKind.Identifier, "loop value variable");
        }

        Expect(TokenKind.In, "in");
        ExprNode collection = ParseExpression();
        Expect(TokenKind.CloseParen, ")");
        AstNode body = ParseStatement();

        return new ForeachNode(RangeFrom(keyword), keyToken, valueToken, collection, body);
    }

    private SwitchNode ParseSwitch()
    {
        PToken keyword = Advance();
        Expect(TokenKind.OpenParen, "(");
        ExprNode subject = ParseExpression();
        Expect(TokenKind.CloseParen, ")");
        Expect(TokenKind.OpenBrace, "{");

        ImmutableArray<CaseGroupNode>.Builder cases = ImmutableArray.CreateBuilder<CaseGroupNode>();

        while ( Kind != TokenKind.CloseBrace && Kind != TokenKind.EndOfFile )
        {
            if ( Kind != TokenKind.Case && Kind != TokenKind.Default )
            {
                AddError(GscDiagnosticCode.ExpectedCaseLabel, Current.RootRange, DescribeCurrent());
                RecoverToCaseLabel();
                continue;
            }

            PToken groupStart = Current;
            ImmutableArray<ExprNode?>.Builder labels = ImmutableArray.CreateBuilder<ExprNode?>();

            // Consecutive labels stack onto one body (fallthrough grouping).
            while ( Kind == TokenKind.Case || Kind == TokenKind.Default )
            {
                if ( Advance().Kind == TokenKind.Case )
                {
                    labels.Add(ParseTernary());
                }
                else
                {
                    labels.Add(null);
                }

                Expect(TokenKind.Colon, ":");
            }

            ImmutableArray<AstNode>.Builder statements = ImmutableArray.CreateBuilder<AstNode>();
            while ( Kind != TokenKind.Case && Kind != TokenKind.Default && Kind != TokenKind.CloseBrace && Kind != TokenKind.EndOfFile )
            {
                statements.Add(ParseStatement());
            }

            cases.Add(new CaseGroupNode(RangeFrom(groupStart), labels.ToImmutable(), statements.ToImmutable()));
        }

        if ( !Match(TokenKind.CloseBrace) )
        {
            AddError(GscDiagnosticCode.UnterminatedBlock, keyword.RootRange);
        }

        return new SwitchNode(RangeFrom(keyword), subject, cases.ToImmutable());
    }

    private ReturnNode ParseReturn()
    {
        PToken keyword = Advance();

        ExprNode? value = null;
        if ( Kind != TokenKind.Semicolon && CanStartExpression(Kind) )
        {
            value = ParseExpression();
        }

        Expect(TokenKind.Semicolon, ";");
        return new ReturnNode(RangeFrom(keyword), value);
    }

    private DevBlockStmtNode ParseDevBlockStatements()
    {
        PToken open = Advance();
        ImmutableArray<AstNode>.Builder statements = ImmutableArray.CreateBuilder<AstNode>();

        while ( Kind != TokenKind.DevBlockClose && Kind != TokenKind.EndOfFile && Kind != TokenKind.CloseBrace )
        {
            statements.Add(ParseStatement());
        }

        if ( !Match(TokenKind.DevBlockClose) )
        {
            AddError(GscDiagnosticCode.UnterminatedDevBlock, open.RootRange);
        }

        return new DevBlockStmtNode(RangeFrom(open), statements.ToImmutable());
    }
}
