using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// Consecutive alignment for the INTERIOR of subscripts and call arguments: a run of statements
/// with the same shape has each bracket column and each argument column padded to its widest.
///
/// <code>
///   foo[ "lol"           ][ "lol2"  ] = "something";
///   foo[ "somethingelse" ][ "other" ] = "garbage";
///
///   register( "toplayer", PARASITE_ROUND_RING_FX  , VERSION_SHIP, 1, "counter" );
///   register( "world"   , "toggle_on_parasite_fog", VERSION_SHIP, 2, "int" );
/// </code>
///
/// It is the same engine for both: two lines share a group when their token SKELETON is identical
/// — the same delimiters and the same fixed anchors (base name, callee, operator) — and only the
/// values in the slots differ. Each slot is a cell; a cell followed by <c>]</c> or <c>,</c> is
/// aligned (its column is padded to the widest), a cell followed by <c>[</c> or <c>(</c> is an
/// anchor that must match, and a cell followed by <c>)</c>, <c>;</c> or an assignment operator is
/// free — it varies but is not padded, which is why the last argument and the right-hand side are
/// left alone.
///
/// Like the other aligners this is a whitespace-only post-pass over already-formatted text, run
/// after the token gate. It REPLACES the gap between a cell and its delimiter rather than inserting
/// into it, computing the gap from the cell's content width, so a second pass finds nothing to do.
/// </summary>
public static class ColumnAligner
{
    /// <param name="maxPadding">
    /// The most spaces alignment may add to any one cell; 0 for no limit. A run that would need
    /// more is left as written, since its columns are too far apart to read as one table.
    /// </param>
    public static string Align(string formatted, int maxPadding = 0)
    {
        string[] lines = formatted.Split('\n');
        ImmutableArray<Token> tokens = Lexer.Lex(SourceText.From(formatted)).Tokens;

        List<Token>[] byLine = LineFacts.BucketByLine(lines.Length, tokens);
        Row[] rows = new Row[lines.Length];
        for ( int i = 0; i < lines.Length; i++ )
        {
            rows[i] = Classify(byLine[i], lines[i]);
        }

        bool changed = false;
        int index = 0;
        while ( index < lines.Length )
        {
            if ( rows[index].Role != RowRole.Alignable )
            {
                index++;
                continue;
            }

            // Gather a run of the same shape at this indent; comments pass through.
            Row first = rows[index];
            List<int> group = LineFacts.GatherRun(index, lines.Length, line => StepOf(rows[line], first));

            int columns = rows[index].Cells.Count;
            if ( group.Count >= 2 && columns > 0 )
            {
                int[] maxWidth = new int[columns];
                int[] baseGap = new int[columns];
                for ( int c = 0; c < columns; c++ )
                {
                    baseGap[c] = int.MaxValue;
                }

                foreach ( int line in group )
                {
                    IReadOnlyList<Cell> cells = rows[line].Cells;
                    for ( int c = 0; c < columns; c++ )
                    {
                        maxWidth[c] = Math.Max(maxWidth[c], cells[c].Width);
                        baseGap[c] = Math.Min(baseGap[c], cells[c].Gap);
                    }
                }

                bool tooWide = false;
                foreach ( int line in group )
                {
                    IReadOnlyList<Cell> cells = rows[line].Cells;
                    for ( int c = 0; c < columns && maxPadding > 0; c++ )
                    {
                        tooWide |= maxWidth[c] - cells[c].Width > maxPadding;
                    }
                }

                List<int> rebuilding = tooWide ? new List<int>() : group;
                foreach ( int line in rebuilding )
                {
                    string rebuilt = Rebuild(lines[line], rows[line].Cells, maxWidth, baseGap);
                    if ( !string.Equals(rebuilt, lines[line], StringComparison.Ordinal) )
                    {
                        lines[line] = rebuilt;
                        changed = true;
                    }
                }
            }

            index = group[^1] + 1;
        }

        return changed ? string.Join('\n', lines) : formatted;
    }

    private static LineFacts.RunStep StepOf(Row row, Row first)
    {
        if ( row.Role == RowRole.Alignable
            && string.Equals(row.Indent, first.Indent, StringComparison.Ordinal)
            && string.Equals(row.Signature, first.Signature, StringComparison.Ordinal) )
        {
            return LineFacts.RunStep.Member;
        }

        return row.Role == RowRole.Comment ? LineFacts.RunStep.Transparent : LineFacts.RunStep.End;
    }

    /// <summary>
    /// Rebuilds a line so each aligned cell's column reaches its target width. The gap between a
    /// cell and its delimiter is REPLACED, not extended, so running twice is a no-op: the new gap
    /// is <c>(maxWidth - width) + baseGap</c>, both of which are stable across passes. Cells are
    /// rewritten right to left so the earlier char offsets stay valid.
    /// </summary>
    private static string Rebuild(string line, IReadOnlyList<Cell> cells, int[] maxWidth, int[] baseGap)
    {
        string result = line;
        for ( int c = cells.Count - 1; c >= 0; c-- )
        {
            Cell cell = cells[c];
            int gap = (maxWidth[c] - cell.Width) + baseGap[c];
            result = string.Concat(
                result.AsSpan(0, cell.ContentEnd),
                new string(' ', gap),
                result.AsSpan(cell.DelimStart));
        }

        return result;
    }

    private enum RowRole
    {
        Other,
        Comment,
        Alignable,
    }

    private readonly record struct Cell(int ContentStart, int ContentEnd, int DelimStart)
    {
        public int Width => ContentEnd - ContentStart;

        public int Gap => DelimStart - ContentEnd;
    }

    private sealed record Row(RowRole Role, string Indent, string Signature, IReadOnlyList<Cell> Cells)
    {
        public static readonly Row Other = new(RowRole.Other, "", "", []);
        public static readonly Row CommentOnly = new(RowRole.Comment, "", "", []);
    }

    private static Row Classify(List<Token> lineTokens, string lineText)
    {
        if ( lineTokens.Count == 0 )
        {
            return Row.Other;
        }

        if ( LineFacts.AllComments(lineTokens) )
        {
            return Row.CommentOnly;
        }

        // A statement ending in ';', ignoring a trailing line comment. Anything else breaks a run.
        List<Token> code = LineFacts.CodeOnly(lineTokens);
        if ( code.Count == 0 || code[^1].Kind != TokenKind.Semicolon )
        {
            return Row.Other;
        }

        // Break the token run into cells (value runs) separated by structural delimiters, then
        // classify each cell by the delimiter that follows it.
        StringBuilder signature = new();
        List<Cell> aligned = [];
        List<Token> cellTokens = [];

        for ( int i = 0; i < code.Count; i++ )
        {
            Token token = code[i];
            if ( !IsStructural(token.Kind) )
            {
                cellTokens.Add(token);
                continue;
            }

            // The structural token closes any open cell; classify it by THIS delimiter.
            if ( cellTokens.Count > 0 )
            {
                CellRole role = RoleOf(token.Kind);
                switch ( role )
                {
                    case CellRole.Anchor:
                        signature.Append(lineText, cellTokens[0].Range.Start.Character,
                            cellTokens[^1].Range.End.Character - cellTokens[0].Range.Start.Character);
                        break;
                    case CellRole.Aligned:
                        signature.Append('\x01');
                        aligned.Add(new Cell(
                            cellTokens[0].Range.Start.Character,
                            cellTokens[^1].Range.End.Character,
                            token.Range.Start.Character));
                        break;
                    default:
                        signature.Append('\x02');
                        break;
                }

                cellTokens.Clear();
            }

            signature.Append(token.Kind == TokenKind.Semicolon ? ";" : TokenText(lineText, token));
        }

        return new Row(RowRole.Alignable, LineFacts.LeadingWhitespace(lineText), signature.ToString(), aligned);
    }

    private enum CellRole
    {
        Anchor,
        Aligned,
        Free,
    }

    private static CellRole RoleOf(TokenKind followingDelimiter)
    {
        switch ( followingDelimiter )
        {
            case TokenKind.Comma:
            case TokenKind.CloseBracket:
                return CellRole.Aligned;
            case TokenKind.OpenBracket:
                // The base of a subscript -- `foo` in `foo[ … ]`. It is aligned, not an anchor, so
                // `foo[ … ]` and `bash[ … ]` still share a shape and the `[` columns line up; the
                // base is padded to the widest.
                return CellRole.Aligned;
            case TokenKind.OpenParen:
                // The callee -- `register` in `register( … )`. This DOES have to match, or calls to
                // different functions would align with each other.
                return CellRole.Anchor;
            default:
                // CloseParen, Semicolon, and every assignment operator.
                return CellRole.Free;
        }
    }

    private static string TokenText(string lineText, Token token)
    {
        return lineText.Substring(token.Range.Start.Character, token.Range.End.Character - token.Range.Start.Character);
    }

    private static bool IsStructural(TokenKind kind)
    {
        if ( TokenFacts.IsAssignmentOperator(kind) )
        {
            return true;
        }

        return kind is TokenKind.OpenParen
            or TokenKind.CloseParen
            or TokenKind.OpenBracket
            or TokenKind.CloseBracket
            or TokenKind.Comma
            or TokenKind.Semicolon;
    }
}
