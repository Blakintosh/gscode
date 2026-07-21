using System.Collections.Immutable;
using System.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Api;

/// <summary>
/// Renders a macro body back into readable GSC for hover.
///
/// Reconstructed from the token stream rather than sliced out of the original source, because
/// a macro reached through <c>#insert</c> lives in a header whose text is not loaded at hover
/// time. That costs the author's exact spacing, but it gains one thing that matters more: line
/// continuations collapse, so a nine-statement macro reads as what it actually expands to
/// instead of a wall of backslashes.
/// </summary>
public static class MacroExpansionPreview
{
    /// <summary>Long bodies are truncated: a hover is a glance, not a code listing.</summary>
    public const int MaxLength = 240;

    /// <summary>The macro's body as a single readable line, or "" for a body-less define.</summary>
    public static string Render(ImmutableArray<PToken> body)
    {
        if ( body.IsDefaultOrEmpty )
        {
            return "";
        }

        StringBuilder text = new();
        for ( int index = 0; index < body.Length; index++ )
        {
            PToken token = body[index];
            if ( text.Length > 0 && NeedsSpaceBefore(token.Kind, body[index - 1].Kind) )
            {
                text.Append(' ');
            }

            text.Append(token.Text);

            if ( text.Length > MaxLength )
            {
                return text.ToString(0, MaxLength).TrimEnd() + " …";
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Spacing heuristic for re-joining tokens. Deliberately simple: this is a preview, so
    /// readable beats faithful, and the rules only need to avoid the jarring cases —
    /// `foo ( a , b ) ;` instead of `foo( a, b );`.
    /// </summary>
    private static bool NeedsSpaceBefore(TokenKind current, TokenKind previous)
    {
        // Nothing hugs an opening delimiter from the inside, and closers/separators hug back.
        if ( current is TokenKind.Semicolon or TokenKind.Comma or TokenKind.CloseParen
            or TokenKind.CloseBracket or TokenKind.Dot or TokenKind.ScopeResolution )
        {
            return false;
        }

        if ( previous is TokenKind.OpenParen or TokenKind.OpenBracket
            or TokenKind.Dot or TokenKind.ScopeResolution or TokenKind.Bang )
        {
            return false;
        }

        // A call's own parenthesis stays attached to its name: `helper( x )`, not `helper ( x )`.
        if ( current == TokenKind.OpenParen && previous is TokenKind.Identifier )
        {
            return false;
        }

        return true;
    }
}
