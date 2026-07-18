using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

public sealed partial class Parser
{
    /// <summary>Top level: directives and declarations in source order.</summary>
    private ScriptNode ParseScript()
    {
        PToken first = Current;
        ImmutableArray<AstNode>.Builder elements = ImmutableArray.CreateBuilder<AstNode>();
        bool sawDeclaration = false;

        while ( Kind != TokenKind.EndOfFile )
        {
            switch ( Kind )
            {
                case TokenKind.UsingDirective:
                {
                    UsingNode usingNode = ParseUsing();
                    if ( sawDeclaration )
                    {
                        AddError(GscDiagnosticCode.UsingAfterDeclaration, usingNode.Range);
                    }

                    elements.Add(usingNode);
                    continue;
                }
                case TokenKind.NamespaceDirective:
                    elements.Add(ParseNamespace());
                    continue;
                case TokenKind.PrecacheDirective:
                    elements.Add(ParsePrecache());
                    continue;
                case TokenKind.UsingAnimTreeDirective:
                    elements.Add(ParseUsingAnimTree());
                    continue;
                case TokenKind.Function:
                    elements.Add(ParseFunction());
                    sawDeclaration = true;
                    continue;
                case TokenKind.Class:
                    elements.Add(ParseClass());
                    sawDeclaration = true;
                    continue;
                case TokenKind.DevBlockOpen:
                    elements.Add(ParseDevBlockDeclarations());
                    continue;
                case TokenKind.Semicolon:
                    Advance();
                    continue;
                default:
                {
                    AddError(GscDiagnosticCode.ExpectedDeclaration, Current.RootRange, DescribeCurrent());
                    RecoverToDeclaration();
                    continue;
                }
            }
        }

        return new ScriptNode(RangeFrom(first), elements.ToImmutable());
    }

    /// <summary>Skips forward to something that can start a declaration; always makes progress.</summary>
    private void RecoverToDeclaration()
    {
        Advance();

        while ( Kind != TokenKind.EndOfFile )
        {
            bool atSyncPoint = Kind is TokenKind.Function
                or TokenKind.Class
                or TokenKind.UsingDirective
                or TokenKind.NamespaceDirective
                or TokenKind.PrecacheDirective
                or TokenKind.UsingAnimTreeDirective
                or TokenKind.DevBlockOpen;

            if ( atSyncPoint )
            {
                return;
            }

            Advance();
        }
    }

    private UsingNode ParseUsing()
    {
        PToken directive = Advance();

        System.Text.StringBuilder path = new();
        PToken? firstPathToken = null;
        PToken? lastPathToken = null;

        while ( IsPathToken(Kind) )
        {
            PToken part = Advance();
            firstPathToken ??= part;
            lastPathToken = part;
            path.Append(part.Text);
        }

        if ( firstPathToken is null )
        {
            AddError(GscDiagnosticCode.ExpectedScriptPath, directive.RootRange, "#using");
        }

        Expect(TokenKind.Semicolon, ";");

        TextRange pathRange = firstPathToken is not null
            ? new TextRange(firstPathToken.Value.RootRange.Start, lastPathToken!.Value.RootRange.End)
            : directive.RootRange;

        return new UsingNode(RangeFrom(directive), path.ToString(), pathRange);
    }

    private static bool IsPathToken(TokenKind kind)
    {
        return kind is TokenKind.Identifier
            or TokenKind.Backslash
            or TokenKind.Slash
            or TokenKind.Dot
            or TokenKind.Integer
            or TokenKind.Minus
            || TokenFacts.IsKeyword(kind);
    }

    private NamespaceNode ParseNamespace()
    {
        PToken directive = Advance();

        PToken nameToken;
        if ( Kind == TokenKind.Identifier || TokenFacts.IsKeyword(Kind) )
        {
            nameToken = Advance();
        }
        else
        {
            AddError(GscDiagnosticCode.ExpectedNamespaceName, directive.RootRange);
            nameToken = new PToken(TokenKind.Identifier, "", directive.RootRange, Provenance.Root);
        }

        Expect(TokenKind.Semicolon, ";");
        return new NamespaceNode(RangeFrom(directive), nameToken);
    }

    private PrecacheNode ParsePrecache()
    {
        PToken directive = Advance();
        Expect(TokenKind.OpenParen, "(");

        // Arguments are kept raw (commas included); P4 validates them against the
        // PrecacheAssetTypes table.
        ImmutableArray<PToken>.Builder arguments = ImmutableArray.CreateBuilder<PToken>();
        int depth = 1;

        while ( Kind != TokenKind.EndOfFile )
        {
            if ( Kind == TokenKind.OpenParen )
            {
                depth++;
            }
            else if ( Kind == TokenKind.CloseParen )
            {
                depth--;
                if ( depth == 0 )
                {
                    Advance();
                    break;
                }
            }

            arguments.Add(Advance());
        }

        Expect(TokenKind.Semicolon, ";");
        return new PrecacheNode(RangeFrom(directive), arguments.ToImmutable());
    }

    private UsingAnimTreeNode ParseUsingAnimTree()
    {
        PToken directive = Advance();
        Expect(TokenKind.OpenParen, "(");

        PToken? treeName = null;
        if ( Kind == TokenKind.String )
        {
            treeName = Advance();
        }
        else
        {
            AddError(GscDiagnosticCode.ExpectedToken, Current.RootRange, "animtree name string", DescribeCurrent());
        }

        Expect(TokenKind.CloseParen, ")");
        Expect(TokenKind.Semicolon, ";");
        return new UsingAnimTreeNode(RangeFrom(directive), treeName);
    }

    private FunctionNode ParseFunction()
    {
        PToken functionKeyword = Advance();

        bool isPrivate = false;
        bool isAutoexec = false;
        while ( Kind == TokenKind.Private || Kind == TokenKind.Autoexec )
        {
            if ( Kind == TokenKind.Private )
            {
                isPrivate = true;
            }
            else
            {
                isAutoexec = true;
            }

            Advance();
        }

        PToken nameToken = Expect(TokenKind.Identifier, "function name");

        ImmutableArray<ParameterNode> parameters = ParseParameterList(out bool hasVarargs);
        BlockNode body = ParseBlock();

        return new FunctionNode(RangeFrom(functionKeyword), nameToken, isPrivate, isAutoexec, parameters, hasVarargs, body);
    }

    private ImmutableArray<ParameterNode> ParseParameterList(out bool hasVarargs)
    {
        hasVarargs = false;
        ImmutableArray<ParameterNode>.Builder parameters = ImmutableArray.CreateBuilder<ParameterNode>();
        Expect(TokenKind.OpenParen, "(");

        while ( Kind != TokenKind.CloseParen && Kind != TokenKind.EndOfFile && Kind != TokenKind.OpenBrace )
        {
            if ( Kind == TokenKind.Ellipsis )
            {
                hasVarargs = true;
                Advance();
            }
            else
            {
                PToken start = Current;
                bool byRef = Match(TokenKind.Ampersand);

                if ( Kind != TokenKind.Identifier )
                {
                    AddError(GscDiagnosticCode.ExpectedParameterName, Current.RootRange, DescribeCurrent());
                    // Skip the offender so the list keeps moving.
                    if ( Kind != TokenKind.CloseParen && Kind != TokenKind.Comma )
                    {
                        Advance();
                    }
                }
                else
                {
                    PToken nameToken = Advance();
                    ExprNode? defaultValue = null;
                    if ( Match(TokenKind.Assign) )
                    {
                        defaultValue = ParseTernary();
                    }

                    parameters.Add(new ParameterNode(RangeFrom(start), nameToken, byRef, defaultValue));
                }
            }

            if ( !Match(TokenKind.Comma) )
            {
                break;
            }
        }

        Expect(TokenKind.CloseParen, ")");
        return parameters.ToImmutable();
    }

    private ClassNode ParseClass()
    {
        PToken classKeyword = Advance();
        PToken nameToken = Expect(TokenKind.Identifier, "class name");

        PToken? parentToken = null;
        if ( Match(TokenKind.Colon) )
        {
            parentToken = Expect(TokenKind.Identifier, "parent class name");
        }

        Expect(TokenKind.OpenBrace, "{");

        ImmutableArray<AstNode>.Builder members = ImmutableArray.CreateBuilder<AstNode>();
        while ( Kind != TokenKind.CloseBrace && Kind != TokenKind.EndOfFile )
        {
            switch ( Kind )
            {
                case TokenKind.Var:
                {
                    PToken varKeyword = Advance();
                    PToken memberName = Expect(TokenKind.Identifier, "member name");
                    Expect(TokenKind.Semicolon, ";");
                    members.Add(new VarDeclNode(RangeFrom(varKeyword), memberName));
                    continue;
                }
                case TokenKind.Constructor:
                {
                    PToken keyword = Advance();
                    ImmutableArray<ParameterNode> parameters = ParseParameterList(out _);
                    BlockNode body = ParseBlock();
                    members.Add(new ConstructorNode(RangeFrom(keyword), keyword, parameters, body));
                    continue;
                }
                case TokenKind.Destructor:
                {
                    PToken keyword = Advance();
                    ImmutableArray<ParameterNode> parameters = ParseParameterList(out _);
                    BlockNode body = ParseBlock();
                    members.Add(new DestructorNode(RangeFrom(keyword), keyword, parameters, body));
                    continue;
                }
                case TokenKind.Function:
                    members.Add(ParseFunction());
                    continue;
                case TokenKind.Semicolon:
                    Advance();
                    continue;
                default:
                {
                    AddError(GscDiagnosticCode.ExpectedClassMember, Current.RootRange, DescribeCurrent());
                    RecoverInsideBraces();
                    continue;
                }
            }
        }

        if ( !Match(TokenKind.CloseBrace) )
        {
            AddError(GscDiagnosticCode.UnterminatedBlock, nameToken.RootRange);
        }

        return new ClassNode(RangeFrom(classKeyword), nameToken, parentToken, members.ToImmutable());
    }

    /// <summary>Skips to the next member-start/closing-brace inside a class body; always progresses.</summary>
    private void RecoverInsideBraces()
    {
        Advance();

        while ( Kind != TokenKind.EndOfFile )
        {
            bool atSyncPoint = Kind is TokenKind.Var
                or TokenKind.Constructor
                or TokenKind.Destructor
                or TokenKind.Function
                or TokenKind.CloseBrace;

            if ( atSyncPoint )
            {
                return;
            }

            Advance();
        }
    }

    private DevBlockDeclNode ParseDevBlockDeclarations()
    {
        PToken open = Advance();
        ImmutableArray<AstNode>.Builder declarations = ImmutableArray.CreateBuilder<AstNode>();

        while ( Kind != TokenKind.DevBlockClose && Kind != TokenKind.EndOfFile )
        {
            switch ( Kind )
            {
                case TokenKind.Function:
                    declarations.Add(ParseFunction());
                    continue;
                case TokenKind.Class:
                    declarations.Add(ParseClass());
                    continue;
                case TokenKind.NamespaceDirective:
                    declarations.Add(ParseNamespace());
                    continue;
                case TokenKind.PrecacheDirective:
                    declarations.Add(ParsePrecache());
                    continue;
                default:
                {
                    AddError(GscDiagnosticCode.ExpectedDeclaration, Current.RootRange, DescribeCurrent());
                    RecoverToDeclarationOrDevClose();
                    continue;
                }
            }
        }

        if ( !Match(TokenKind.DevBlockClose) )
        {
            AddError(GscDiagnosticCode.UnterminatedDevBlock, open.RootRange);
        }

        return new DevBlockDeclNode(RangeFrom(open), declarations.ToImmutable());
    }

    private void RecoverToDeclarationOrDevClose()
    {
        Advance();

        while ( Kind != TokenKind.EndOfFile )
        {
            bool atSyncPoint = Kind is TokenKind.Function
                or TokenKind.Class
                or TokenKind.NamespaceDirective
                or TokenKind.PrecacheDirective
                or TokenKind.DevBlockClose;

            if ( atSyncPoint )
            {
                return;
            }

            Advance();
        }
    }
}
