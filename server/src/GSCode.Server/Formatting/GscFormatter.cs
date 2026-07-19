using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// A whitespace-only GSC/CSC formatter. It emits every non-trivia token verbatim and only
/// recomputes the whitespace around them: Allman braces, one statement per line, 4-space
/// indent from brace depth, padded control-flow and non-empty parentheses, and blank lines
/// capped at two. Comments, dev blocks, macros, and disabled branches pass through untouched.
///
/// Two safety properties make it impossible to corrupt code: it refuses to format a file
/// with lex/parse errors, and it re-lexes its own output and returns the original unchanged
/// if the non-trivia token stream is not byte-for-byte identical to the input's.
/// </summary>
public static class GscFormatter
{
    private const int IndentWidth = 4;

    /// <summary>
    /// Produces the formatted document text, or null when formatting is refused (syntax
    /// errors) or would not be safe (the token stream would change). A null result means
    /// "make no edits".
    /// </summary>
    public static string? Format(ParseResult result)
    {
        if ( HasSyntaxErrors(result) )
        {
            return null;
        }

        ImmutableArray<Token> tokens = result.Lexed.Tokens;
        List<SignificantToken> significant = CollectSignificant(tokens, result.Text);
        if ( significant.Count == 0 )
        {
            return null;
        }

        string formatted = Reflow(significant, result.Text);

        // Corruption guard: the reflow must preserve the exact non-trivia token stream.
        if ( !TokenStreamMatches(significant, result.Text, formatted) )
        {
            return null;
        }

        return formatted;
    }

    /// <summary>One significant (non-trivia) token plus how many newlines preceded it in the source.</summary>
    private readonly record struct SignificantToken(Token Token, int NewlinesBefore);

    private static List<SignificantToken> CollectSignificant(ImmutableArray<Token> tokens, SourceText text)
    {
        List<SignificantToken> significant = [];
        int newlineRun = 0;

        foreach ( Token token in tokens )
        {
            if ( token.Kind == TokenKind.Newline )
            {
                newlineRun++;
                continue;
            }

            if ( token.Kind == TokenKind.Whitespace || token.Kind == TokenKind.EndOfFile )
            {
                continue;
            }

            significant.Add(new SignificantToken(token, newlineRun));
            newlineRun = 0;
        }

        return significant;
    }

    private static string Reflow(List<SignificantToken> significant, SourceText text)
    {
        StringBuilder output = new();
        int depth = 0;

        for ( int index = 0; index < significant.Count; index++ )
        {
            Token token = significant[index].Token;
            int newlinesBefore = significant[index].NewlinesBefore;

            // Closers dedent before this line's indent is computed.
            if ( token.Kind == TokenKind.CloseBrace || token.Kind == TokenKind.DevBlockClose )
            {
                depth = Math.Max(0, depth - 1);
            }

            if ( index == 0 )
            {
                output.Append(token.GetText(text));
            }
            else
            {
                Token previous = significant[index - 1].Token;
                bool trailingComment = IsComment(token.Kind) && newlinesBefore == 0;

                if ( ShouldBreak(previous.Kind, token.Kind, newlinesBefore, trailingComment) )
                {
                    int blankLines = Math.Clamp(newlinesBefore - 1, 0, 1);
                    output.Append('\n', 1 + blankLines);
                    output.Append(' ', depth * IndentWidth);
                }
                else
                {
                    output.Append(Separator(previous.Kind, token.Kind));
                }

                output.Append(token.GetText(text));
            }

            // Openers indent everything that follows.
            if ( token.Kind == TokenKind.OpenBrace || token.Kind == TokenKind.DevBlockOpen )
            {
                depth++;
            }
        }

        output.Append('\n');
        return output.ToString();
    }

    /// <summary>
    /// Decides whether the token starts a new line. Structural breaks (Allman braces, one
    /// statement per line) are forced; otherwise an original line break is preserved, which
    /// keeps newline-terminated directives (#define, #if) intact. A trailing comment stays
    /// glued to the line it annotated.
    /// </summary>
    private static bool ShouldBreak(TokenKind previous, TokenKind current, int newlinesBefore, bool trailingComment)
    {
        if ( trailingComment )
        {
            return false;
        }

        // A line comment runs to end-of-line, so whatever follows must start a new line.
        if ( previous == TokenKind.LineComment )
        {
            return true;
        }

        if ( current == TokenKind.OpenBrace || current == TokenKind.CloseBrace || current == TokenKind.DevBlockClose )
        {
            return true;
        }

        if ( previous == TokenKind.OpenBrace || previous == TokenKind.CloseBrace || previous == TokenKind.DevBlockOpen )
        {
            return true;
        }

        if ( previous == TokenKind.Semicolon )
        {
            return true;
        }

        return newlinesBefore > 0;
    }

    /// <summary>The intra-line separator between two adjacent tokens: a single space or nothing.</summary>
    private static string Separator(TokenKind previous, TokenKind current)
    {
        // Parenthesis interior padding: "( x )", but "()" stays tight.
        if ( previous == TokenKind.OpenParen )
        {
            return current == TokenKind.CloseParen ? "" : " ";
        }

        if ( current == TokenKind.CloseParen )
        {
            return " ";
        }

        // Brackets hug their contents: a[0], [[ptr]].
        if ( previous == TokenKind.OpenBracket || current == TokenKind.OpenBracket || current == TokenKind.CloseBracket )
        {
            return "";
        }

        if ( NoSpaceAfter(previous) )
        {
            return "";
        }

        if ( NoSpaceBefore(current) )
        {
            return "";
        }

        // A call/declaration '(' hugs its callee/name; a control-flow '(' is padded.
        if ( current == TokenKind.OpenParen )
        {
            return IsControlFlowKeyword(previous) ? " " : "";
        }

        return " ";
    }

    private static bool NoSpaceAfter(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Dot:
            case TokenKind.ScopeResolution:
            case TokenKind.Arrow:
            case TokenKind.Backslash:
            case TokenKind.Bang:
            case TokenKind.Tilde:
            case TokenKind.Hash:
            case TokenKind.Dollar:
                return true;
            default:
                return false;
        }
    }

    private static bool NoSpaceBefore(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Semicolon:
            case TokenKind.Comma:
            case TokenKind.Dot:
            case TokenKind.ScopeResolution:
            case TokenKind.Arrow:
            case TokenKind.Backslash:
            case TokenKind.PlusPlus:
            case TokenKind.MinusMinus:
            case TokenKind.Colon:
                return true;
            default:
                return false;
        }
    }

    private static bool IsControlFlowKeyword(TokenKind kind)
    {
        return kind is TokenKind.If
            or TokenKind.While
            or TokenKind.For
            or TokenKind.Foreach
            or TokenKind.Switch;
    }

    private static bool IsComment(TokenKind kind)
    {
        return kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment;
    }

    /// <summary>Refuses formatting when the file has lexer (1xxx) or parser (3xxx) errors.</summary>
    private static bool HasSyntaxErrors(ParseResult result)
    {
        foreach ( Diagnostic diagnostic in result.AllDiagnostics )
        {
            if ( diagnostic.Severity != DiagnosticSeverity.Error )
            {
                continue;
            }

            int code = (int)diagnostic.Code;
            bool lexError = code >= 1000 && code < 2000;
            bool parseError = code >= 3000 && code < 4000;
            if ( lexError || parseError )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the formatted output lexes to the same non-trivia token stream (kinds and
    /// exact text) as the input. This is the corruption guard — any mismatch aborts the edit.
    /// </summary>
    private static bool TokenStreamMatches(List<SignificantToken> input, SourceText inputText, string formatted)
    {
        SourceText formattedText = SourceText.From(formatted);
        List<SignificantToken> output = CollectSignificant(Lexer.Lex(formattedText).Tokens, formattedText);

        if ( input.Count != output.Count )
        {
            return false;
        }

        for ( int index = 0; index < input.Count; index++ )
        {
            Token before = input[index].Token;
            Token after = output[index].Token;
            if ( before.Kind != after.Kind )
            {
                return false;
            }

            if ( !before.GetText(inputText).SequenceEqual(after.GetText(formattedText)) )
            {
                return false;
            }
        }

        return true;
    }
}
