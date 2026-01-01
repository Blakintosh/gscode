using GSCode.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.Data;

namespace GSCode.Parser.Lexical;

internal record class Token(TokenType Type, Range Range, string Lexeme)
{
    /// <summary>
    /// Whether this token is from a preprocessor expansion, which suppresses some IntelliSense
    /// features like hovers and semantic highlighting.
    /// </summary>
    public bool IsFromPreprocessor { get; init; }

    /// <summary>
    /// If the token is from the preprocessor, this stores the range that the token occurs
    /// in the original document.
    /// </summary>
    public Range? SourceRange { get; init; }

    /// <summary>
    /// Stores reference to the next token in the sequence.
    /// </summary>
    public Token Next { get; set; } = default!;

    /// <summary>
    /// Stores reference to the previous token in the sequence.
    /// </summary>
    public Token Previous { get; set; } = default!;

    /// <summary>
    /// When specified, the token symbol has IntelliSense information associated with it,
    /// such as a hoverable and semantic highlighting.
    /// </summary>
    public ISenseDefinition? SenseDefinition { get; set; } = default!;

    public int Length => Lexeme.Length;

    public bool IsWhitespacey()
    {
        return Type == TokenType.Whitespace
            || Type == TokenType.LineBreak
            || Type == TokenType.Backslash
            || Type == TokenType.LineComment
            || Type == TokenType.MultilineComment
            || Type == TokenType.DocComment;
    }

    public bool IsComment()
    {
        return Type == TokenType.LineComment
            || Type == TokenType.MultilineComment
            || Type == TokenType.DocComment;
    }

    public bool IsKeyword()
    {
        return Type == TokenType.Classes ||
               Type == TokenType.Function ||
               Type == TokenType.Var ||
               Type == TokenType.Return ||
               Type == TokenType.Thread ||
               Type == TokenType.Class ||
               Type == TokenType.Anim ||
               Type == TokenType.If ||
               Type == TokenType.Else ||
               Type == TokenType.Do ||
               Type == TokenType.While ||
               Type == TokenType.Foreach ||
               Type == TokenType.For ||
               Type == TokenType.In ||
               Type == TokenType.New ||
               Type == TokenType.Switch ||
               Type == TokenType.Case ||
               Type == TokenType.Default ||
               Type == TokenType.Break ||
               Type == TokenType.Continue ||
               Type == TokenType.Constructor ||
               Type == TokenType.Destructor ||
               Type == TokenType.Autoexec ||
               Type == TokenType.Private ||
               Type == TokenType.Const;
    }
}

/// <summary>
/// Container to represent a sequence of tokens that are ultimately linked.
/// </summary>
/// <param name="Start">The beginning token of the sequence.</param>
/// <param name="End">The ending token of the sequence.</param>
internal record struct TokenList(Token? Start, Token? End)
{
    public readonly Range? Range { get; } = Start != null && End != null ? RangeHelper.From(Start.Range.Start, End.Range.End) : default;

    public static TokenList Empty => new(null, null);

    public readonly bool IsEmpty => Start == null || End == null;

    public static TokenList From(params Token[] tokens)
    {
        if (tokens.Length == 0)
        {
            return Empty;
        }

        // Fully clone the tokens so we have no shared references
        Token[] clonedTokens = new Token[tokens.Length];

        for (int i = 0; i < clonedTokens.Length; i++)
        {
            clonedTokens[i] = tokens[i] with { Range = RangeHelper.Empty };

            clonedTokens[i].Previous = i > 0 ? clonedTokens[i - 1] : null!;
            if (i - 1 >= 0)
            {
                clonedTokens[i - 1].Next = clonedTokens[i];
            }
        }
        Token start = clonedTokens[0];
        Token end = clonedTokens[^1];

        return new TokenList(start, end);
    }

    public readonly void ConnectToTokens(Token before, Token after)
    {
        // Nothing to connect
        if (Start == null || End == null)
        {
            before.Next = after;
            after.Previous = before;
            return;
        }

        // Otherwise connect our list
        before.Next = Start;
        Start.Previous = before;

        End.Next = after;
        after.Previous = End;
    }

    public TokenList CloneList(Range? withRange = null)
    {
        if (Start == null || End == null)
        {
            return Empty;
        }

        Token currentTokenFromExpansion = Start;
        // Populate the first token.
        Token firstToken = currentTokenFromExpansion with { Range = withRange ?? currentTokenFromExpansion.Range };
        Token lastToken = firstToken;

        while (currentTokenFromExpansion != End)
        {
            currentTokenFromExpansion = currentTokenFromExpansion.Next;

            // Clone the current token with the updated range
            Token currentToken = currentTokenFromExpansion with { Range = withRange ?? currentTokenFromExpansion.Range };

            // Connect the cloned token to the previous one in the output chain
            lastToken.Next = currentToken;
            currentToken.Previous = lastToken;

            // Update the previous token reference and move to the next token
            lastToken = currentToken;
        }

        return new TokenList(firstToken, lastToken);
    }

    /// <summary>
    /// Produces a clean string of the snippet of raw code this token list represents.
    /// </summary>
    /// <returns></returns>
    public readonly string ToSnippetString()
    {
        if (Start == null || End == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        Token current = FirstNonWhitespaceToken()!;
        Token last = LastNonWhitespaceToken()!;
        bool lastAddedWhitespace = false;

        do
        {
            // Only ever add one whitespace token in a chain of them, so we don't get snippets with multiple spaces
            if (!lastAddedWhitespace || !current.IsWhitespacey())
            {
                lastAddedWhitespace = current.IsWhitespacey();
                // If we've reached whitespace, just emit a single space and don't do this repeatedly
                sb.Append(lastAddedWhitespace ? ' ' : current.Lexeme);
            }

            // Go to next
            if (current == last)
            {
                break;
            }
            current = current.Next;
        } while (current is not null && current.Type != TokenType.Eof);

        if (current.Type == TokenType.Eof)
        {
            Console.Error.WriteLine("sanity check failed");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Produces a source-exact string of the snippet of raw code this token list represents.
    /// </summary>
    /// <returns></returns>
    public readonly string ToRawString()
    {
        if (Start == null || End == null)
        {
            return string.Empty;
        }

        StringBuilder sb = new();

        Token current = Start;
        Token last = End;

        do
        {
            sb.Append(current.Lexeme);

            // Go to next
            if (current == last)
            {
                break;
            }
            current = current.Next;
        } while (current is not null);

        return sb.ToString();
    }

    public readonly Token? FirstNonWhitespaceToken()
    {
        if (Start == null || End == null)
        {
            return null;
        }

        // Get the first non-whitespace token, otherwise the last if they're all whitespace
        Token current = Start;
        while (current.Type == TokenType.Whitespace && current != End)
        {
            current = current.Next;
        }

        return current;
    }

    public readonly Token? LastNonWhitespaceToken()
    {
        if (Start == null || End == null)
        {
            return null;
        }

        // Get the last non-whitespace token, otherwise the first if they're all whitespace
        Token current = End;
        while (current.Type == TokenType.Whitespace && current != Start)
        {
            current = current.Previous;
        }

        return current;
    }
}

internal enum TokenType
{
    // Misc
    Sof,
    Eof,
    Whitespace,
    LineBreak,
    Unknown,

    // Error types
    ErrorString,

    // Comments
    LineComment,
    MultilineComment,
    DocComment,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    OpenDevBlock,
    CloseDevBlock,

    // Operators
    IdentityNotEquals, // !==
    IdentityEquals, // ===
    ScopeResolution, // ::
    Dot, // .
    Arrow, // ->
    And, // &&
    BitLeftShiftAssign, // <<=
    BitRightShiftAssign, // >>=
    BitAndAssign, // &=
    BitOrAssign, // |=
    BitXorAssign, // ^=
    DivideAssign, // /=
    MinusAssign, // -=
    ModuloAssign, // %=
    MultiplyAssign, // *=
    PlusAssign, // +=
    BitLeftShift, // <<
    BitRightShift, // >>
    Decrement, // --
    Equals, // ==
    GreaterThanEquals, // >=
    Increment, // ++
    LessThanEquals, // <=
    NotEquals, // !=
    Or, // ||
    Assign, // =
    BitAnd, // &
    BitNot, // ~
    BitOr, // |
    BitXor, // ^
    Divide, // /
    GreaterThan, // >
    LessThan, // <
    Minus, // -
    Modulo, // %
    Multiply, // *
    Not, // !
    Plus, // +
    QuestionMark, // ?
    Colon, // :

    // Special Tokens
    Semicolon, // ;
    Comma, // ,
    VarargDots, // ...
    Backslash, // \
    Hash, // #

    // Keywords
    Classes, // classes
    Function, // function
    Var, // var
    Return, // return
    Thread, // thread
    Class, // class
    Anim, // anim
    If, // if
    Else, // else
    Do, // do
    While, // while
    Foreach, // foreach
    For, // for
    In, // in
    New, // new
    Switch, // switch
    Case, // case
    Default, // default
    Break, // break
    Continue, // continue
    Constructor, // constructor
    Destructor, // destructor
    Autoexec, // autoexec
    Private, // private
    Const, // const

    // Preprocessor keywords
    UsingAnimTree, // #using_animtree
    Using, // #using
    Insert, // #insert
    Define, // #define
    Namespace, // #namespace
    Precache, // #precache
    PreIf, // #if
    PreElIf, // #elif
    PreElse, // #else
    PreEndIf, // #endif


    // Reserved functions (case-insensitive) TODO
    WaittillFrameEnd, // waittillframeend
    WaittillMatch, // waittillmatch
    Waittill, // waittill
    WaitRealTime, // waitrealtime
    Wait, // wait
    //AssertMsg, // assertmsg
    //Assert, // assert
    //VectorScale, // vectorscale
    //GetTime, // getttime
    //ProfileStart, // profilestart
    //ProfileStop, // profilestop

    // Literals
    Undefined, // undefined
    False, // false
    True, // true
    String, // "string"
    // ReSharper disable once InconsistentNaming
    IString, // &"string"
    CompilerHash, // #"string"
    Integer, // 123
    Float, // 123.456
    Hex, // 0x123
    AnimTree, // #animtree

    // Identifier
    Identifier, // name
    AnimIdentifier, // %name
}