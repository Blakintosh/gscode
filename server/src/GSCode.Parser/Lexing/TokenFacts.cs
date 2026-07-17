namespace GSCode.Parser.Lexing;

/// <summary>
/// Fixed lexemes for token kinds whose text never varies (operators, punctuation,
/// directives), so materializing their text allocates nothing. Kinds with variable
/// text (identifiers, keywords with user casing, literals) return null.
/// </summary>
public static class TokenFacts
{
    /// <summary>
    /// True for keyword kinds. Relies on the keyword block in TokenKind being contiguous
    /// from Class through ProfileStop — the enum documents this ordering requirement.
    /// </summary>
    public static bool IsKeyword(TokenKind kind)
    {
        return kind >= TokenKind.Class && kind <= TokenKind.ProfileStop;
    }

    /// <summary>The canonical lexeme for a fixed-text kind, or null when the source must be sliced.</summary>
    public static string? GetStaticText(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.UsingDirective: return "#using";
            case TokenKind.InsertDirective: return "#insert";
            case TokenKind.DefineDirective: return "#define";
            case TokenKind.NamespaceDirective: return "#namespace";
            case TokenKind.PrecacheDirective: return "#precache";
            case TokenKind.UsingAnimTreeDirective: return "#using_animtree";
            case TokenKind.AnimTreeDirective: return "#animtree";
            case TokenKind.IfDirective: return "#if";
            case TokenKind.ElifDirective: return "#elif";
            case TokenKind.ElseDirective: return "#else";
            case TokenKind.EndifDirective: return "#endif";
            case TokenKind.DevBlockOpen: return "/#";
            case TokenKind.DevBlockClose: return "#/";
            case TokenKind.OpenParen: return "(";
            case TokenKind.CloseParen: return ")";
            case TokenKind.OpenBrace: return "{";
            case TokenKind.CloseBrace: return "}";
            case TokenKind.OpenBracket: return "[";
            case TokenKind.CloseBracket: return "]";
            case TokenKind.Semicolon: return ";";
            case TokenKind.Comma: return ",";
            case TokenKind.Dot: return ".";
            case TokenKind.Ellipsis: return "...";
            case TokenKind.Colon: return ":";
            case TokenKind.ScopeResolution: return "::";
            case TokenKind.QuestionMark: return "?";
            case TokenKind.Backslash: return "\\";
            case TokenKind.Hash: return "#";
            case TokenKind.Dollar: return "$";
            case TokenKind.Arrow: return "->";
            case TokenKind.Assign: return "=";
            case TokenKind.Plus: return "+";
            case TokenKind.Minus: return "-";
            case TokenKind.Star: return "*";
            case TokenKind.Slash: return "/";
            case TokenKind.Percent: return "%";
            case TokenKind.Ampersand: return "&";
            case TokenKind.Pipe: return "|";
            case TokenKind.Caret: return "^";
            case TokenKind.Tilde: return "~";
            case TokenKind.Bang: return "!";
            case TokenKind.LessThan: return "<";
            case TokenKind.GreaterThan: return ">";
            case TokenKind.PlusPlus: return "++";
            case TokenKind.MinusMinus: return "--";
            case TokenKind.EqualsEquals: return "==";
            case TokenKind.StrictEquals: return "===";
            case TokenKind.NotEquals: return "!=";
            case TokenKind.StrictNotEquals: return "!==";
            case TokenKind.LessThanEquals: return "<=";
            case TokenKind.GreaterThanEquals: return ">=";
            case TokenKind.LogicalAnd: return "&&";
            case TokenKind.LogicalOr: return "||";
            case TokenKind.ShiftLeft: return "<<";
            case TokenKind.ShiftRight: return ">>";
            case TokenKind.PlusAssign: return "+=";
            case TokenKind.MinusAssign: return "-=";
            case TokenKind.StarAssign: return "*=";
            case TokenKind.SlashAssign: return "/=";
            case TokenKind.PercentAssign: return "%=";
            case TokenKind.AmpersandAssign: return "&=";
            case TokenKind.PipeAssign: return "|=";
            case TokenKind.CaretAssign: return "^=";
            case TokenKind.ShiftLeftAssign: return "<<=";
            case TokenKind.ShiftRightAssign: return ">>=";
            case TokenKind.EndOfFile: return "";
            default: return null;
        }
    }
}
