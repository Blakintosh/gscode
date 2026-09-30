using System.Collections.Immutable;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// The line-level questions every aligner asks before it can decide anything: which tokens sit on
/// which line, is a token a comment, what is a line indented by, and what does it look like with
/// its comments removed.
///
/// Extracted because each was written out separately in <see cref="AssignmentAligner"/>,
/// <see cref="ColumnAligner"/>, <see cref="FormatScope"/> and <see cref="GscFormatter"/> — four
/// identical bodies for <c>IsComment</c>, three for <c>LeadingWhitespace</c>, three for
/// <c>BucketByLine</c>. They are the premises of the alignment rules rather than rules themselves,
/// and a premise that disagrees between two aligners is a formatter that contradicts itself.
/// </summary>
internal static class LineFacts
{
    /// <summary>Whether a token is any kind of comment. All three kinds count.</summary>
    public static bool IsComment(TokenKind kind)
    {
        return kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment;
    }

    /// <summary>
    /// A line's leading spaces and tabs, verbatim. Returned as the original TEXT rather than a
    /// width, because the aligners compare indentation for equality and re-emit it unchanged —
    /// converting to a width would silently normalise tabs against spaces.
    /// </summary>
    public static string LeadingWhitespace(string line)
    {
        int end = 0;
        while ( end < line.Length && (line[end] == ' ' || line[end] == '\t') )
        {
            end++;
        }

        return line[..end];
    }

    /// <summary>
    /// The line's tokens with comments dropped, so a trailing <c>// note</c> does not stop
    /// <c>a = 1; // note</c> from being recognised as an assignment.
    /// </summary>
    public static List<Token> CodeOnly(List<Token> lineTokens)
    {
        return [.. lineTokens.Where(static token => !IsComment(token.Kind))];
    }

    /// <summary>Whether every token on the line is a comment (a comment-only line).</summary>
    public static bool AllComments(List<Token> lineTokens)
    {
        return lineTokens.All(static token => IsComment(token.Kind));
    }

    /// <summary>
    /// The index of the first assignment operator outside every parenthesis, bracket and brace, or
    /// -1. An operator inside a subscript or an argument list belongs to that expression, not to
    /// the statement.
    /// </summary>
    public static int TopLevelAssignment(List<Token> code)
    {
        int depth = 0;
        for ( int i = 0; i < code.Count; i++ )
        {
            TokenKind kind = code[i].Kind;
            switch ( kind )
            {
                case TokenKind.OpenParen:
                case TokenKind.OpenBracket:
                case TokenKind.OpenBrace:
                    depth++;
                    break;
                case TokenKind.CloseParen:
                case TokenKind.CloseBracket:
                case TokenKind.CloseBrace:
                    depth--;
                    break;
                default:
                    if ( depth == 0 && TokenFacts.IsAssignmentOperator(kind) )
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>What a line does to a run of lines an aligner is gathering.</summary>
    public enum RunStep
    {
        /// <summary>The line belongs to the run.</summary>
        Member,

        /// <summary>The line neither joins the run nor ends it: a comment on its own line.</summary>
        Transparent,

        /// <summary>The line ends the run.</summary>
        End,
    }

    /// <summary>
    /// The lines of the run starting at <paramref name="start"/>, which the caller has already
    /// found to be a member. Transparent lines are stepped over but not returned, so the run ends
    /// at its last member even when comments follow it.
    /// </summary>
    public static List<int> GatherRun(int start, int lineCount, Func<int, RunStep> step)
    {
        List<int> members = [start];
        for ( int line = start + 1; line < lineCount; line++ )
        {
            RunStep answer = step(line);
            if ( answer == RunStep.End )
            {
                break;
            }

            if ( answer == RunStep.Member )
            {
                members.Add(line);
            }
        }

        return members;
    }

    /// <summary>
    /// The significant tokens of each line, indexed by line number. Every line gets a list, empty
    /// or not, so callers can index straight into it without a bounds check.
    /// </summary>
    /// <remarks>
    /// Whitespace and newlines are lexed as tokens and are dropped here. They carry no meaning for
    /// classifying a line, and keeping them would put a line's terminator behind a trailing
    /// Newline — which is what every caller's "does this line end in a semicolon" test reads.
    /// </remarks>
    public static List<Token>[] BucketByLine(int lineCount, ImmutableArray<Token> tokens)
    {
        List<Token>[] byLine = new List<Token>[lineCount];
        for ( int i = 0; i < lineCount; i++ )
        {
            byLine[i] = [];
        }

        foreach ( Token token in tokens )
        {
            if ( token.Kind is TokenKind.EndOfFile or TokenKind.Whitespace or TokenKind.Newline )
            {
                continue;
            }

            int line = token.Range.Start.Line;
            if ( line >= 0 && line < lineCount )
            {
                byLine[line].Add(token);
            }
        }

        return byLine;
    }
}
