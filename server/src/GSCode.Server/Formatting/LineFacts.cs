using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// The line-level questions every aligner asks before it can decide anything: is this token a
/// comment, what is a line indented by, and what does it look like with its comments removed.
///
/// Extracted because all three were written out separately in <see cref="AssignmentAligner"/>,
/// <see cref="ColumnAligner"/>, <see cref="FormatScope"/> and <see cref="GscFormatter"/> — four
/// identical bodies for <c>IsComment</c> and three for <c>LeadingWhitespace</c>. They are the
/// premises of the alignment rules rather than rules themselves, and a premise that disagrees
/// between two aligners is a formatter that contradicts itself.
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
}
