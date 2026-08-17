using System.Buffers;
using System.Collections.Immutable;

namespace GSCode.Core.Text;

/// <summary>
/// An immutable text snapshot with a precomputed line-start index, giving O(log lines)
/// offset-to-position mapping. Offsets and characters are UTF-16 code units throughout.
/// </summary>
public sealed class SourceText
{
    private static readonly SearchValues<char> s_lineBreaks = SearchValues.Create("\r\n");

    /// <summary>The full document text.</summary>
    public string Text { get; }

    // Offset of the first character of each line. Line 0 always starts at offset 0.
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string text, ImmutableArray<int> lineStarts)
    {
        Text = text;
        _lineStarts = lineStarts;
    }

    /// <summary>Total length in UTF-16 code units.</summary>
    public int Length
    {
        get { return Text.Length; }
    }

    /// <summary>Number of lines (always at least 1, even for empty text).</summary>
    public int LineCount
    {
        get { return _lineStarts.Length; }
    }

    /// <summary>Builds a snapshot from raw text, scanning once for line breaks (\r\n, \n, or lone \r).</summary>
    public static SourceText From(string text)
    {
        // Sized for a 24-character line so the builder does not double its way up from empty. Only
        // a rough guide — being wrong costs one growth, being unset costs a chain of them.
        const int typicalLineLength = 24;

        ImmutableArray<int>.Builder lineStarts =
            ImmutableArray.CreateBuilder<int>(1 + (text.Length / typicalLineLength));
        lineStarts.Add(0);

        int index = 0;
        while ( index < text.Length )
        {
            // The text BETWEEN breaks is the bulk of a file and needs no inspection, so it is
            // searched a vector at a time rather than a character at a time.
            int relative = text.AsSpan(index).IndexOfAny(s_lineBreaks);
            if ( relative < 0 )
            {
                break;
            }

            int breakIndex = index + relative;

            // \r\n is one break, so a \r that has a \n after it ends at the \n.
            if ( text[breakIndex] == '\r' && breakIndex + 1 < text.Length && text[breakIndex + 1] == '\n' )
            {
                breakIndex++;
            }

            lineStarts.Add(breakIndex + 1);
            index = breakIndex + 1;
        }

        return new SourceText(text, lineStarts.ToImmutable());
    }

    /// <summary>Converts a UTF-16 offset into a line/character position (binary search over line starts).</summary>
    public Position GetPosition(int offset)
    {
        if ( offset < 0 )
        {
            offset = 0;
        }
        else if ( offset > Text.Length )
        {
            offset = Text.Length;
        }

        int line = FindLineContaining(offset);
        return new Position(line, offset - _lineStarts[line]);
    }

    /// <summary>
    /// Converts an offset into a position, resuming from <paramref name="lineHint"/> and leaving it
    /// on the line that was found. A caller that walks the text FORWARD pays the lines it crossed
    /// rather than a binary search per call, so a whole file costs O(lines) instead of
    /// O(lookups x log lines) — which is what the lexer does, twice per token including trivia.
    ///
    /// The hint is an optimisation and never a correctness requirement: an offset behind it falls
    /// back to the binary search, so a stale or wrong hint costs time and changes no answer.
    /// </summary>
    public Position GetPosition(int offset, ref int lineHint)
    {
        if ( offset < 0 )
        {
            offset = 0;
        }
        else if ( offset > Text.Length )
        {
            offset = Text.Length;
        }

        if ( lineHint < 0 || lineHint >= _lineStarts.Length || _lineStarts[lineHint] > offset )
        {
            lineHint = FindLineContaining(offset);
            return new Position(lineHint, offset - _lineStarts[lineHint]);
        }

        int line = lineHint;
        while ( line + 1 < _lineStarts.Length && _lineStarts[line + 1] <= offset )
        {
            line++;
        }

        lineHint = line;
        return new Position(line, offset - _lineStarts[line]);
    }

    /// <summary>Converts a position back into a UTF-16 offset, clamping to valid bounds.</summary>
    public int GetOffset(Position position)
    {
        if ( position.Line < 0 )
        {
            return 0;
        }

        if ( position.Line >= _lineStarts.Length )
        {
            return Text.Length;
        }

        int offset = _lineStarts[position.Line] + Math.Max(0, position.Character);
        return Math.Min(offset, Text.Length);
    }

    /// <summary>Returns the offset where the given line begins.</summary>
    public int GetLineStart(int line)
    {
        return _lineStarts[line];
    }

    /// <summary>A span view over part of the text, avoiding substring allocation.</summary>
    public ReadOnlySpan<char> Slice(int start, int length)
    {
        return Text.AsSpan(start, length);
    }

    private int FindLineContaining(int offset)
    {
        int low = 0;
        int high = _lineStarts.Length - 1;

        while ( low < high )
        {
            int middle = (low + high + 1) / 2;
            if ( _lineStarts[middle] <= offset )
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low;
    }
}
