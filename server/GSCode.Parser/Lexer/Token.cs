using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Lexer;

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
}

internal enum TokenType
{
    // Misc
    Sof,
    Eof,
    Whitespace,
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