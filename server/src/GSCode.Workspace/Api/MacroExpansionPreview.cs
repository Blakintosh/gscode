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
        return Render(body, [], []);
    }

    /// <summary>
    /// The macro's body with the CALL SITE's arguments substituted for its parameters, so hovering
    /// <c>IS_TRUE( foo )</c> reads <c>isdefined( foo ) &amp;&amp; foo</c> rather than
    /// <c>isdefined( __a ) &amp;&amp; __a</c>.
    ///
    /// Substitution is per TOKEN, not textual: a parameter named <c>a</c> replaced by text would
    /// also rewrite the <c>a</c> inside <c>value</c>. Parameters the call did not supply keep
    /// their own names, which is the honest thing to show for a half-written invocation.
    /// </summary>
    public static string Render(
        ImmutableArray<PToken> body, ImmutableArray<string> parameters, ImmutableArray<string> arguments)
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

            text.Append(Substitute(token, parameters, arguments));

            if ( text.Length > MaxLength )
            {
                return text.ToString(0, MaxLength).TrimEnd() + " …";
            }
        }

        return text.ToString();
    }

    /// <summary>The argument this token stands for, or the token's own text.</summary>
    private static string Substitute(PToken token, ImmutableArray<string> parameters, ImmutableArray<string> arguments)
    {
        if ( token.Kind != TokenKind.Identifier || parameters.IsDefaultOrEmpty )
        {
            return token.Text;
        }

        for ( int index = 0; index < parameters.Length && index < arguments.Length; index++ )
        {
            // Macro parameter names are case-sensitive, as macro names themselves are.
            if ( string.Equals(parameters[index], token.Text, StringComparison.Ordinal) )
            {
                return arguments[index];
            }
        }

        return token.Text;
    }

    /// <summary>
    /// The argument list written at a call site, split on top-level commas.
    ///
    /// Read from the invocation's own text because a MacroInvocation records WHERE it is and WHAT
    /// it calls, but not what it was passed. Nesting is respected, so
    /// <c>OUTER( inner( a, b ), c )</c> yields two arguments rather than three.
    /// </summary>
    public static ImmutableArray<string> ParseArguments(string invocationText)
    {
        int open = invocationText.IndexOf('(');
        if ( open < 0 )
        {
            return [];
        }

        ImmutableArray<string>.Builder arguments = ImmutableArray.CreateBuilder<string>();
        StringBuilder current = new();
        int depth = 0;

        for ( int index = open; index < invocationText.Length; index++ )
        {
            char c = invocationText[index];

            if ( c is '(' or '[' )
            {
                depth++;
                if ( depth == 1 )
                {
                    // The opening parenthesis is not part of the first argument.
                    continue;
                }
            }
            else if ( c is ')' or ']' )
            {
                depth--;
                if ( depth == 0 )
                {
                    break;
                }
            }
            else if ( c == ',' && depth == 1 )
            {
                arguments.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        string last = current.ToString().Trim();
        if ( last.Length > 0 || arguments.Count > 0 )
        {
            arguments.Add(last);
        }

        return arguments.ToImmutable();
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
