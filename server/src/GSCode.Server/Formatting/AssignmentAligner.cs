using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// Consecutive alignment for assignments: a run of assignment statements at the same indentation
/// has its operators lined up, one space past the longest left-hand side.
///
/// <code>
///   level.wasp_enabled          = true;
///   level.wasp_round_count_blah = 1;      // longest LHS sets the column
///   level.wasp_round_count      += 1;     // compound operators extend rightward from it
/// </code>
///
/// This is a deliberate override of the stock scripts, which align almost nothing (2 assignments in
/// the whole corpus), so it is off in <see cref="FormatOptions.Default"/> and driven by a setting.
///
/// It runs as a post-pass on already-reflowed text, like <see cref="DirectiveSorter"/>, and for the
/// same reason: it changes only the whitespace between a left-hand side and its operator, never a
/// token, so the token-equality gate has already done its job and this cannot undo it. The output
/// is re-lexed rather than scanned as text, so a `=` inside a string, a block comment, or a dev
/// block is never mistaken for an operator.
///
/// Grouping (a user decision): a blank line or a statement of a different kind ends a run; a
/// comment on its own line is transparent and an aligned run continues across it. Only runs of two
/// or more assignments are touched.
/// </summary>
public static class AssignmentAligner
{
    public static string Align(string formatted)
    {
        string[] lines = formatted.Split('\n');
        ImmutableArray<Token> tokens = Lexer.Lex(SourceText.From(formatted)).Tokens;

        LineKind[] kinds = ClassifyLines(lines.Length, tokens, lines);

        StringBuilder output = new();
        int index = 0;
        bool changed = false;
        while ( index < lines.Length )
        {
            if ( kinds[index].Kind != LineRole.Assignment )
            {
                output.Append(lines[index]);
                if ( index < lines.Length - 1 )
                {
                    output.Append('\n');
                }

                index++;
                continue;
            }

            // Gather a run of assignments at this indent, letting comment lines pass through.
            string indent = kinds[index].Indent;
            List<int> group = [];
            int scan = index;
            int lastAssignment = index;
            while ( scan < lines.Length )
            {
                LineKind kind = kinds[scan];
                if ( kind.Kind == LineRole.Assignment && string.Equals(kind.Indent, indent, StringComparison.Ordinal) )
                {
                    group.Add(scan);
                    lastAssignment = scan;
                    scan++;
                }
                else if ( kind.Kind == LineRole.Comment )
                {
                    // Transparent: it neither aligns nor breaks the run.
                    scan++;
                }
                else
                {
                    break;
                }
            }

            int target = 0;
            foreach ( int line in group )
            {
                target = Math.Max(target, kinds[line].LeftLength);
            }

            target += 1;

            // Emit every line from index through the last aligned assignment, re-padding the
            // assignments and passing the interleaved comments straight through.
            for ( int line = index; line <= lastAssignment; line++ )
            {
                if ( kinds[line].Kind == LineRole.Assignment
                    && string.Equals(kinds[line].Indent, indent, StringComparison.Ordinal)
                    && group.Count >= 2 )
                {
                    string aligned = Repad(lines[line], kinds[line], target);
                    if ( !string.Equals(aligned, lines[line], StringComparison.Ordinal) )
                    {
                        changed = true;
                    }

                    output.Append(aligned);
                }
                else
                {
                    output.Append(lines[line]);
                }

                if ( line < lines.Length - 1 )
                {
                    output.Append('\n');
                }
            }

            index = lastAssignment + 1;
        }

        return changed ? output.ToString() : formatted;
    }

    private static string Repad(string line, LineKind kind, int target)
    {
        string left = line[..kind.OperatorColumn].TrimEnd();
        string rest = line[kind.OperatorColumn..];
        int padding = Math.Max(1, target - left.Length);
        return left + new string(' ', padding) + rest;
    }

    private enum LineRole
    {
        Other,
        Comment,
        Assignment,
    }

    private readonly record struct LineKind(LineRole Kind, string Indent, int LeftLength, int OperatorColumn);

    private static LineKind[] ClassifyLines(int lineCount, ImmutableArray<Token> tokens, string[] lines)
    {
        // Bucket the significant tokens by their start line.
        List<Token>[] byLine = new List<Token>[lineCount];
        for ( int i = 0; i < lineCount; i++ )
        {
            byLine[i] = [];
        }

        foreach ( Token token in tokens )
        {
            // Whitespace and newlines are lexed as tokens; they carry no meaning for classifying a
            // line, and keeping them would put the terminator behind a trailing Newline.
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

        LineKind[] kinds = new LineKind[lineCount];
        for ( int i = 0; i < lineCount; i++ )
        {
            kinds[i] = Classify(byLine[i], lines[i]);
        }

        return kinds;
    }

    private static LineKind Classify(List<Token> lineTokens, string lineText)
    {
        if ( lineTokens.Count == 0 )
        {
            // Blank, or a continuation line of a block comment: either way, breaks a run.
            return new LineKind(lineText.Trim().Length == 0 ? LineRole.Other : LineRole.Other, "", 0, 0);
        }

        // A comment on its own line is transparent.
        if ( lineTokens.All(static token => LineFacts.IsComment(token.Kind)) )
        {
            return new LineKind(LineRole.Comment, "", 0, 0);
        }

        // Drop a trailing line comment; `a = 1; // note` is still an assignment.
        List<Token> code = [.. lineTokens.Where(static token => !LineFacts.IsComment(token.Kind))];
        if ( code.Count < 3 || code[^1].Kind != TokenKind.Semicolon )
        {
            return new LineKind(LineRole.Other, "", 0, 0);
        }

        // The statement must be exactly one: a single terminating semicolon, and no braces or
        // stray semicolons that would mean this line is something other than `lhs op rhs;`.
        int depth = 0;
        int operatorIndex = -1;
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
                case TokenKind.Semicolon:
                    if ( i != code.Count - 1 )
                    {
                        // A second statement on the line: not our shape.
                        return new LineKind(LineRole.Other, "", 0, 0);
                    }

                    break;
                default:
                    if ( depth == 0 && operatorIndex < 0 && TokenFacts.IsAssignmentOperator(kind) )
                    {
                        operatorIndex = i;
                    }

                    break;
            }
        }

        // Need an assignment operator at top level, with a left-hand side before it.
        if ( operatorIndex <= 0 )
        {
            return new LineKind(LineRole.Other, "", 0, 0);
        }

        int operatorColumn = code[operatorIndex].Range.Start.Character;
        string left = lineText[..operatorColumn].TrimEnd();
        string indent = LineFacts.LeadingWhitespace(lineText);

        return new LineKind(LineRole.Assignment, indent, left.Length, operatorColumn);
    }
}
