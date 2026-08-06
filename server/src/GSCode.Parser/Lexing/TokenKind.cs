namespace GSCode.Parser.Lexing;

/// <summary>
/// Every kind of token the lexer can produce. Trivia (whitespace, comments) are tokens
/// too — the parser skips them via TokenCursor, while the formatter and semantic tokens
/// read the raw stream. Note: [[ and ]] are NOT lexed as double-bracket tokens; the
/// parser recognizes two adjacent brackets, so nested indexers like a[b[1]] just work.
/// </summary>
public enum TokenKind
{
    // Sentinels
    EndOfFile,
    Error,

    // Trivia
    Whitespace,
    Newline,
    LineComment,
    BlockComment,
    DocComment,

    // Literals and names. Globals (self, level, game, ...) lex as Identifier and are
    // classified downstream, matching engine behavior.
    Identifier,
    Integer,
    Float,
    Hex,
    String,
    LocalizedString,
    HashString,
    AnimReference,

    // Keywords (matched case-insensitively). ORDERING CONTRACT: this block must stay
    // contiguous from Class through ProfileStop — TokenFacts.IsKeyword range-checks it.
    Class,
    Function,
    Var,
    Return,
    Wait,
    WaitTill,
    WaitTillMatch,
    WaitTillFrameEnd,
    WaitRealTime,
    Thread,
    // childthread (MW2+, Infinity Ward line) spawns a child thread; call (MW2+) invokes a function
    // pointer synchronously (call [[ ptr ]]( … )). Distinct kinds from Thread so they highlight as
    // their own keywords and a dialect that lacks them keeps the word as an identifier.
    ChildThread,
    Call,
    /// <summary>
    /// The running thread itself (MW2, Infinity Ward line): <c>self.trackLoopThread = thisthread;</c>.
    /// A keyword rather than an identifier so it colours as one and cannot be renamed, but like
    /// <see cref="Vararg"/> it appears in EXPRESSION position, so it parses as an
    /// <see cref="Syntax.Ast.IdentifierNode"/> wrapping this token. Kept inside the keyword block so
    /// <c>TokenFacts.IsKeyword</c>'s range check accepts it; gated by the dialect's keyword set, so on
    /// a game without it the word stays an identifier and a script may still use it as a variable.
    /// </summary>
    ThisThread,
    If,
    Else,
    Do,
    While,
    For,
    Foreach,
    In,
    New,
    Switch,
    Case,
    Default,
    Break,
    Continue,
    Notify,
    Endon,
    Assert,
    AssertMsg,
    Constructor,
    Destructor,
    Autoexec,
    Private,
    Const,
    IsDefined,
    Undefined,
    True,
    False,
    VectorScale,
    ProfileStart,
    ProfileStop,

    /// <summary>
    /// The parameter pack a <c>...</c> declaration binds. A keyword rather than an identifier so it
    /// colours as one and cannot be renamed, but it parses as an <see cref="Syntax.Ast.IdentifierNode"/>
    /// wrapping this token — the same shape the callable keywords use — because unlike every other
    /// keyword here it appears in EXPRESSION position: <c>foreach ( f in vararg )</c>, <c>vararg.size</c>.
    /// Gated by the dialect's keyword set, so on a game without the pack it stays an identifier and a
    /// script may still use the word as a variable name.
    /// </summary>
    Vararg,

    // Preprocessor directives (matched case-sensitively, lowercase per engine convention)
    UsingDirective,
    IncludeDirective,
    InsertDirective,
    DefineDirective,
    NamespaceDirective,
    PrecacheDirective,
    UsingAnimTreeDirective,
    AnimTreeDirective,
    IfDirective,
    ElifDirective,
    ElseDirective,
    EndifDirective,

    // Dev blocks
    DevBlockOpen,
    DevBlockClose,

    // Punctuation
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    Semicolon,
    Comma,
    Dot,
    Ellipsis,
    Colon,
    ScopeResolution,
    QuestionMark,
    Backslash,
    Hash,
    Dollar,
    Arrow,

    // Operators
    Assign,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    Ampersand,
    Pipe,
    Caret,
    Tilde,
    Bang,
    LessThan,
    GreaterThan,
    PlusPlus,
    MinusMinus,
    EqualsEquals,
    StrictEquals,
    NotEquals,
    StrictNotEquals,
    LessThanEquals,
    GreaterThanEquals,
    LogicalAnd,
    LogicalOr,
    ShiftLeft,
    ShiftRight,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    AmpersandAssign,
    PipeAssign,
    CaretAssign,
    ShiftLeftAssign,
    ShiftRightAssign,
}
