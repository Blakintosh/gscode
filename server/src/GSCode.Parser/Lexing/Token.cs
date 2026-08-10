using GSCode.Core.Text;

namespace GSCode.Parser.Lexing;

/// <summary>
/// One lexed token: its kind, its UTF-16 offset span in the source, and its precomputed
/// line/character range. Tokens never own text — read it via <see cref="GetText"/>.
/// </summary>
public readonly record struct Token(TokenKind Kind, int Start, int Length, TextRange Range)
{
    /// <summary>One past the last character of the token (half-open, like Range).</summary>
    public int End
    {
        get { return Start + Length; }
    }

    /// <summary>True for tokens the parser skips: whitespace, newlines, and all comment forms.</summary>
    public bool IsTrivia
    {
        get
        {
            return Kind is TokenKind.Whitespace
                or TokenKind.Newline
                or TokenKind.LineComment
                or TokenKind.BlockComment
                or TokenKind.DocComment;
        }
    }

    /// <summary>The token's text as a span over the source (no allocation).</summary>
    public ReadOnlySpan<char> GetText(SourceText text)
    {
        return text.Slice(Start, Length);
    }
}
