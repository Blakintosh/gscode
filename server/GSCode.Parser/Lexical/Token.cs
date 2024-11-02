using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Lexical;

internal record class Token(TokenType type, Range range, string lexeme)
{
    public TokenType Type { get; } = type;
    public Range Range { get; } = range;
    public string Lexeme { get; } = lexeme;

    /// <summary>
    /// Stores reference to the next token in the sequence.
    /// </summary>
    public Token Next { get; set; } = default!;

    /// <summary>
    /// Stores reference to the previous token in the sequence.
    /// </summary>
    public Token Previous { get; set; } = default!;

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
}

/// <summary>
/// Container to represent a sequence of tokens that are ultimately linked.
/// </summary>
/// <param name="Start">The beginning token of the sequence.</param>
/// <param name="End">The ending token of the sequence.</param>
internal record struct TokenList(Token Start, Token End)
{
    /// <summary>
    /// Produces a string of the snippet of raw code this token list represents.
    /// </summary>
    /// <returns></returns>
    public readonly string ToSnippetString()
    {
        StringBuilder sb = new();

        Token current = FirstNonWhitespaceToken();
        Token last = LastNonWhitespaceToken();
        bool lastAddedWhitespace = false;

        do
        {
            // Only ever add one whitespace token in a chain of them, so we don't get snippets with multiple spaces
            if(!lastAddedWhitespace || !current.IsWhitespacey())
            {
                lastAddedWhitespace = current.IsWhitespacey();
                // If we've reached whitespace, just emit a single space and don't do this repeatedly
                sb.Append(lastAddedWhitespace ? ' ' : current.Lexeme);
            }

            // Go to next
            if (current != last)
            {
                current = current.Next;
            }
        } while (current != last);

        return sb.ToString();
    }

    public readonly Token FirstNonWhitespaceToken()
    {
        // Get the first non-whitespace token, otherwise the last if they're all whitespace
        Token current = Start;
        while (current.Type == TokenType.Whitespace && current != End)
        {
            current = current.Next;
        }

        return current;
    }

    public readonly Token LastNonWhitespaceToken()
    {
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