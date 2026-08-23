using System.Collections.Immutable;
using System.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Api;

/// <summary>
/// One argument of a macro invocation, as offsets into the file's text: <c>[Start, End)</c>,
/// already trimmed of the whitespace around it.
/// </summary>
/// <param name="Start">Offset of the argument's first character.</param>
/// <param name="End">Offset one past its last.</param>
public readonly record struct MacroArgumentSpan(int Start, int End);

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
    /// The argument list following a macro NAME at <paramref name="afterName"/>, split on
    /// top-level commas.
    ///
    /// A MacroInvocation's range covers the name alone — <c>IS_TRUE</c>, not <c>IS_TRUE( v )</c> —
    /// so the arguments have to be read from the text that follows it. Anything other than an
    /// opening parenthesis next means the macro is object-like and takes none.
    /// </summary>
    public static ImmutableArray<string> ArgumentsFollowing(string text, int afterName)
    {
        return Texts(text, ArgumentSpansFollowing(text, afterName));
    }

    /// <summary>
    /// Where each of those arguments IS, rather than what it says — the trimmed offsets of every
    /// top-level argument following the name at <paramref name="afterName"/>.
    ///
    /// Inlay hints need the position and not the text: a <c>__a:</c> label goes immediately before
    /// the argument it names. Sharing the scan with <see cref="ArgumentsFollowing"/> is what keeps
    /// the hint on the same argument the hover claims it is.
    /// </summary>
    public static ImmutableArray<MacroArgumentSpan> ArgumentSpansFollowing(string text, int afterName)
    {
        int scan = afterName;
        while ( scan < text.Length && char.IsWhiteSpace(text[scan]) )
        {
            scan++;
        }

        if ( scan >= text.Length || text[scan] != '(' )
        {
            return [];
        }

        return SpansFrom(text, scan);
    }

    /// <summary>
    /// The argument list written at a call site, split on top-level commas.
    ///
    /// Read from the call site's text because a MacroInvocation records WHERE it is and WHAT it
    /// calls, but not what it was passed. Nesting is respected, so
    /// <c>OUTER( inner( a, b ), c )</c> yields two arguments rather than three.
    /// </summary>
    public static ImmutableArray<string> ParseArguments(string invocationText)
    {
        int open = invocationText.IndexOf('(');
        if ( open < 0 )
        {
            return [];
        }

        return Texts(invocationText, SpansFrom(invocationText, open));
    }

    /// <summary>The text each span covers.</summary>
    private static ImmutableArray<string> Texts(string text, ImmutableArray<MacroArgumentSpan> spans)
    {
        return [.. spans.Select(span => text[span.Start..span.End])];
    }

    /// <summary>
    /// The top-level argument spans of the parenthesised list opening at <paramref name="open"/>,
    /// each already trimmed of surrounding whitespace.
    ///
    /// Depth counts brackets as well as parentheses, so <c>things[0, 1]</c> stays one argument. An
    /// unterminated list — the normal state while typing — yields what has been written so far.
    /// </summary>
    private static ImmutableArray<MacroArgumentSpan> SpansFrom(string text, int open)
    {
        ImmutableArray<MacroArgumentSpan>.Builder spans = ImmutableArray.CreateBuilder<MacroArgumentSpan>();
        int depth = 0;
        int start = open + 1;

        for ( int index = open; index < text.Length; index++ )
        {
            char c = text[index];

            if ( c is '(' or '[' )
            {
                depth++;
            }
            else if ( c is ')' or ']' )
            {
                depth--;
                if ( depth == 0 )
                {
                    // `FOO()` takes none; `FOO( a, )` really was written with a second, empty one.
                    AddSpan(spans, text, start, index, keepEmpty: spans.Count > 0);
                    return spans.ToImmutable();
                }
            }
            else if ( c == ',' && depth == 1 )
            {
                AddSpan(spans, text, start, index, keepEmpty: true);
                start = index + 1;
            }
        }

        AddSpan(spans, text, start, text.Length, keepEmpty: spans.Count > 0);
        return spans.ToImmutable();
    }

    /// <summary>Adds [start, end) with its surrounding whitespace trimmed off both ends.</summary>
    private static void AddSpan(
        ImmutableArray<MacroArgumentSpan>.Builder spans, string text, int start, int end, bool keepEmpty)
    {
        while ( start < end && char.IsWhiteSpace(text[start]) )
        {
            start++;
        }

        while ( end > start && char.IsWhiteSpace(text[end - 1]) )
        {
            end--;
        }

        if ( start < end || keepEmpty )
        {
            spans.Add(new MacroArgumentSpan(start, end));
        }
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
