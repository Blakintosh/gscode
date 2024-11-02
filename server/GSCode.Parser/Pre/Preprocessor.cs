using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Pre;

internal ref struct Preprocessor(Token startToken, ParserIntelliSense sense)
{
    public Token CurrentToken { get; private set; } = startToken;

    public readonly TokenType CurrentTokenType => CurrentToken.Type;
    public readonly Range CurrentTokenRange => CurrentToken.Range;

    public ParserIntelliSense Sense { get; } = sense;

    public Dictionary<string, MacroDefinition> Defines { get; } = new();

    /// <summary>
    /// Performs preprocessor transformations on the script token sequence.
    /// </summary>
    public void Transform()
    {
        while(CurrentTokenType != TokenType.Eof)
        {
            switch(CurrentTokenType)
            {
                case TokenType.Define:
                    Define();
                    break;
                case TokenType.Insert:
                    Insert();
                    break;
                case TokenType.PreIf:
                    If();
                    break;
                case TokenType.PreElIf:
                    // TODO: try to consume an expression for better fault tolerance
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective);
                    Advance();
                    break;
                case TokenType.PreElse:
                case TokenType.PreEndIf:
                    AddError(GSCErrorCodes.MisplacedPreprocessorDirective);

                    // Delete the directive so it doesn't cause further issues
                    ConnectTokens(CurrentToken.Previous, CurrentToken.Next);
                    Advance();
                    break;
                default:
                    Advance();
                    break;
            }
        }
    }

    /// <summary>
    /// Transforms a macro definition into a script define.
    /// </summary>
    private void Define()
    {
        // Pass over DEFINE
        Token defineToken = Consume();

        // Get the macro name
        if (!ConsumeIfType(TokenType.Identifier, out Token? macroNameToken))
        {
            AddError(GSCErrorCodes.ExpectedMacroIdentifier, CurrentToken.Lexeme);
            return;
        }

        string macroName = macroNameToken.Lexeme;

        // Get its parameter list, if it has one.
        IEnumerable<Token> parameters = ParamList();

        // Consume its expansion continuously until we get to a final line break
        Token start = CurrentToken;
        Token last = CurrentToken;

        while(CurrentTokenType != TokenType.LineBreak && CurrentTokenType != TokenType.Eof)
        {
            last = Consume();

            // Handle backslash here, which must immediately precede a line break if encountered
            if(ConsumeIfType(TokenType.Backslash, out Token? backslashToken))
            {
                if(backslashToken.Next.Type != TokenType.LineBreak)
                {
                    AddError(GSCErrorCodes.InvalidLineContinuation, "\\");
                }
                else
                {
                    // Advance to skip the line break if it was fine
                    Advance();
                }
            }
        }

        // Consume skips comments, so if there is one at the end of this expansion we can get it directly from working backwards from CurrentToken
        Token documentationToken = CurrentToken.Previous;
        string? documentation = null;

        // TODO: this currently doesn't remove the //, etc.
        if (documentationToken.IsComment())
        {
            documentation = documentationToken.Lexeme;
        }

        // Create the macro
        Defines[macroName] = new MacroDefinition(
            macroNameToken,
            DefineTokens: new TokenList(defineToken, last),
            ExpansionTokens: new TokenList(start, last),
            Parameters: parameters,
            Documentation: documentation
            );
    }

    /// <summary>
    /// Parses a macro definition's parameter list, if it exists.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> ParamList()
    {
        // No parameters
        if(!AdvanceIfType(TokenType.OpenParen))
        {
            return [];
        }

        LinkedList<Token> parameters = Params();

        // Check for CLOSEPAREN
        if(!AdvanceIfType(TokenType.CloseParen))
        {
            AddError(GSCErrorCodes.ExpectedPreprocessorToken, ")", CurrentToken.Lexeme);
        }

        return parameters;
    }

    /// <summary>
    /// Parses zero or more macro definition parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> Params()
    {
        // Zero parameters
        if(!ConsumeIfType(TokenType.Identifier, out Token? parameterToken))
        {
            return [];
        }

        // One or more parameters
        LinkedList<Token> rest = ParamsRhs();
        rest.AddFirst(parameterToken);

        return rest;
    }

    /// <summary>
    /// Parses the right-hand side of a macro definition's parameters.
    /// </summary>
    /// <returns></returns>
    private LinkedList<Token> ParamsRhs()
    {
        // End of parameter list
        if (!ConsumeIfType(TokenType.Comma, out Token? commaToken))
        {
            return new();
        }

        // Get the next parameter's name
        if (!ConsumeIfType(TokenType.Identifier, out Token? parameterToken))
        {
            AddError(GSCErrorCodes.ExpectedMacroParameter, CurrentToken.Lexeme);
            return new();
        }

        // Recurse
        LinkedList<Token> rest = ParamsRhs();
        rest.AddFirst(parameterToken);

        return rest;
    }

    private void Insert()
    {
        // Pass over INSERT
        Token insertToken = Consume();


    }

    private void ConnectTokens(Token left, Token right)
    {
        left.Next = right;
        right.Previous = left;
    }


    private void AddError(GSCErrorCodes errorCode, params object?[] args)
    {
        Sense.AddPreDiagnostic(CurrentTokenRange, errorCode, args);
    }

    private void Advance()
    {
        do
        {
            CurrentToken = CurrentToken.Next;
        }
        // Ignore all whitespace and comments, but don't ignore line breaks.
        while (
            CurrentTokenType == TokenType.Whitespace ||
            CurrentTokenType == TokenType.LineComment ||
            CurrentTokenType == TokenType.MultilineComment ||
            CurrentTokenType == TokenType.DocComment);
    }

    private Token Consume()
    {
        Token consumed = CurrentToken;
        Advance();

        return consumed;
    }

    private bool AdvanceIfType(TokenType type)
    {
        if (CurrentTokenType == type)
        {
            Advance();
            return true;
        }

        return false;
    }

    private bool ConsumeIfType(TokenType type, [NotNullWhen(true)] out Token? consumed)
    {
        Token current = CurrentToken;
        if (AdvanceIfType(type))
        {
            consumed = current;
            return true;
        }

        consumed = default;
        return false;
    }
}
