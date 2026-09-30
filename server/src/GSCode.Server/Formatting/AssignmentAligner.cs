using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// Consecutive alignment for assignments: a run of assignment statements at the same indentation
/// has its '=' lined up, one space past the longest left-hand side.
///
/// <code>
///   level.wasp_enabled          = true;
///   level.wasp_round_count_blah = 1;      // longest LHS sets the column
///   level.wasp_round_count     += 1;      // a compound operator's '=' shares it too
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
    /// <param name="maxPadding">
    /// The most spaces alignment may add to any one line; 0 for no limit. A run whose left-hand
    /// sides differ by more sheds its outliers, which keep a single space, and the rest align.
    /// </param>
    public static string Align(string formatted, int maxPadding = 0)
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
            List<int> group = LineFacts.GatherRun(index, lines.Length, line => StepOf(kinds[line], indent));
            int lastAssignment = group[^1];

            // The lines that take part: all of them, unless aligning would pad one past the cap.
            HashSet<int> aligned = WithinPadding(group, kinds, maxPadding);

            // Every '=' lands in one column: one space past the longest left-hand side, or further
            // when a compound operator's leading characters would not otherwise fit before it.
            int equalsColumn = 0;
            foreach ( int line in aligned )
            {
                equalsColumn = Math.Max(equalsColumn, EqualsWidth(kinds[line]));
            }

            // Emit every line from index through the last aligned assignment, re-padding the
            // assignments and passing the interleaved comments straight through.
            for ( int line = index; line <= lastAssignment; line++ )
            {
                if ( kinds[line].Kind == LineRole.Assignment
                    && string.Equals(kinds[line].Indent, indent, StringComparison.Ordinal)
                    && group.Count >= 2 )
                {
                    // A compound operator starts early by its length before the '=', so `+=`
                    // hangs its '+' one column left of the shared '='. A line left out of the
                    // alignment keeps a single space.
                    int operatorStart = aligned.Contains(line) && aligned.Count >= 2
                        ? equalsColumn - (kinds[line].OperatorLength - 1)
                        : kinds[line].LeftLength + 1;
                    string repadded = Repad(lines[line], kinds[line], operatorStart);
                    if ( !string.Equals(repadded, lines[line], StringComparison.Ordinal) )
                    {
                        changed = true;
                    }

                    output.Append(repadded);
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

    /// <summary>Where a line's '=' sits with a single space before its operator.</summary>
    private static int EqualsWidth(LineKind kind)
    {
        return kind.LeftLength + kind.OperatorLength;
    }

    /// <summary>
    /// The lines of a run that align together without any of them gaining more than
    /// <paramref name="maxPadding"/> spaces. While the run's widest and narrowest sides are too far
    /// apart, whichever of the two is further from the median leaves — so one long outlier
    /// (`nextID = …` beside a 98-character subscript chain) drops out and the rest still align.
    /// </summary>
    private static HashSet<int> WithinPadding(List<int> group, LineKind[] kinds, int maxPadding)
    {
        List<int> kept = [.. group];
        while ( maxPadding > 0 && kept.Count >= 2 )
        {
            List<int> byWidth = [.. kept.OrderBy(line => EqualsWidth(kinds[line]))];
            int narrowest = byWidth[0];
            int widest = byWidth[^1];
            int spread = EqualsWidth(kinds[widest]) - EqualsWidth(kinds[narrowest]);
            if ( spread <= maxPadding )
            {
                break;
            }

            int median = EqualsWidth(kinds[byWidth[byWidth.Count / 2]]);
            bool widestIsFurther = EqualsWidth(kinds[widest]) - median >= median - EqualsWidth(kinds[narrowest]);
            kept.Remove(widestIsFurther ? widest : narrowest);
        }

        return [.. kept];
    }

    private static LineFacts.RunStep StepOf(LineKind kind, string indent)
    {
        if ( kind.Kind == LineRole.Assignment && string.Equals(kind.Indent, indent, StringComparison.Ordinal) )
        {
            return LineFacts.RunStep.Member;
        }

        return kind.Kind == LineRole.Comment ? LineFacts.RunStep.Transparent : LineFacts.RunStep.End;
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

    private readonly record struct LineKind(
        LineRole Kind, string Indent, int LeftLength, int OperatorColumn, int OperatorLength = 1);

    private static LineKind[] ClassifyLines(int lineCount, ImmutableArray<Token> tokens, string[] lines)
    {
        List<Token>[] byLine = LineFacts.BucketByLine(lineCount, tokens);

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
            return new LineKind(LineRole.Other, "", 0, 0);
        }

        // A comment on its own line is transparent.
        if ( LineFacts.AllComments(lineTokens) )
        {
            return new LineKind(LineRole.Comment, "", 0, 0);
        }

        // Drop a trailing line comment; `a = 1; // note` is still an assignment.
        List<Token> code = LineFacts.CodeOnly(lineTokens);
        if ( code.Count < 3 || code[^1].Kind != TokenKind.Semicolon )
        {
            return new LineKind(LineRole.Other, "", 0, 0);
        }

        // The statement must be exactly one: a second semicolon means this line is something other
        // than `lhs op rhs;`.
        for ( int i = 0; i < code.Count - 1; i++ )
        {
            if ( code[i].Kind == TokenKind.Semicolon )
            {
                return new LineKind(LineRole.Other, "", 0, 0);
            }
        }

        // Need an assignment operator at top level, with a left-hand side before it.
        int operatorIndex = LineFacts.TopLevelAssignment(code);
        if ( operatorIndex <= 0 )
        {
            return new LineKind(LineRole.Other, "", 0, 0);
        }

        Token operatorToken = code[operatorIndex];
        int operatorColumn = operatorToken.Range.Start.Character;
        int operatorLength = operatorToken.Range.End.Character - operatorColumn;
        string left = lineText[..operatorColumn].TrimEnd();
        string indent = LineFacts.LeadingWhitespace(lineText);

        return new LineKind(LineRole.Assignment, indent, left.Length, operatorColumn, operatorLength);
    }
}
