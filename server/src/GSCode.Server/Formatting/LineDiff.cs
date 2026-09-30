namespace GSCode.Server.Formatting;

/// <summary>An original line and the formatted line it became.</summary>
internal readonly record struct LinePair(int Original, int Formatted);

/// <summary>
/// Which original lines correspond to which formatted lines, compared by each line's CONTENT KEY —
/// its text with every whitespace character removed and lowercased.
/// </summary>
/// <remarks>
/// The formatter changes whitespace and, with fixCasing, letter case; it never changes what the file
/// says. So joined end to end, the original lines' keys and the formatted lines' keys spell the same
/// string, and each line is an interval of it. Two lines correspond when their intervals are the
/// same; where the formatter split a line or joined some, the intervals re-meet a little further on,
/// and the lines between are an edit of their own. That is one pass, exact, and indifferent to how
/// much the formatter changed — a diff over the lines instead costs time with every added line, and
/// a script whose every `if ( x ) { y(); }` splits into five lines went past any sensible budget and
/// came back as one edit for the whole file.
///
/// The one pass that breaks the equality is directive sorting, which moves lines — and only within
/// the leading directive block. So the region after the last difference is matched by interval, and
/// only what is left above it goes to a Myers diff, which on a directive block is a few dozen lines.
/// </remarks>
internal static class LineDiff
{
    /// <summary>
    /// Past this many insertions and deletions the Myers diff over the leading region gives up, and
    /// that region becomes one edit. The rest of the file is matched by interval regardless.
    /// </summary>
    private const int MaxDifferences = 2000;

    /// <summary>A line's content with every whitespace character removed, lowercased.</summary>
    public static string KeyOf(string line)
    {
        System.Text.StringBuilder key = new(line.Length);
        foreach ( char character in line )
        {
            if ( !char.IsWhiteSpace(character) )
            {
                key.Append(char.ToLowerInvariant(character));
            }
        }

        return key.ToString();
    }

    /// <summary>The matched lines, in order. Unmatched lines between two matches are an edit.</summary>
    public static List<LinePair> Match(IReadOnlyList<string> original, IReadOnlyList<string> formatted)
    {
        // How far each line's content sits from the END of the file, in key characters: the side
        // the two texts agree on once the leading directive block is behind them.
        int[] originalFromEnd = DistancesFromEnd(original);
        int[] formattedFromEnd = DistancesFromEnd(formatted);
        int commonTail = CommonKeySuffix(original, formatted);

        // The first line on each side lying wholly inside the shared tail, advanced until both
        // start at the same distance from the end, so the two regions spell the same keys.
        int originalSplit = FirstLineWithin(originalFromEnd, original, commonTail);
        int formattedSplit = FirstLineWithin(formattedFromEnd, formatted, commonTail);
        while ( originalSplit < original.Count && formattedSplit < formatted.Count
            && originalFromEnd[originalSplit] != formattedFromEnd[formattedSplit] )
        {
            if ( originalFromEnd[originalSplit] > formattedFromEnd[formattedSplit] )
            {
                originalSplit++;
            }
            else
            {
                formattedSplit++;
            }
        }

        // Lines both sides agree on just above the split belong with the region below it, where they
        // pair directly; left above it, a blank line there could be read as deleted on one side and
        // inserted on the other.
        while ( originalSplit > 0 && formattedSplit > 0
            && string.Equals(original[originalSplit - 1], formatted[formattedSplit - 1], StringComparison.Ordinal) )
        {
            originalSplit--;
            formattedSplit--;
        }

        List<LinePair> pairs = [];
        if ( originalSplit > 0 || formattedSplit > 0 )
        {
            List<LinePair>? head = Myers(original, formatted, originalSplit, formattedSplit);
            if ( head is not null )
            {
                pairs.AddRange(head);
            }
        }

        MatchByInterval(original, formatted, originalSplit, formattedSplit, pairs);
        return pairs;
    }

    private static int[] DistancesFromEnd(IReadOnlyList<string> keys)
    {
        int[] distances = new int[keys.Count + 1];
        for ( int index = keys.Count - 1; index >= 0; index-- )
        {
            distances[index] = distances[index + 1] + keys[index].Length;
        }

        return distances;
    }

    /// <summary>How many key characters, counted from the end, the two texts share.</summary>
    private static int CommonKeySuffix(IReadOnlyList<string> original, IReadOnlyList<string> formatted)
    {
        int shared = 0;
        int originalLine = original.Count - 1;
        int formattedLine = formatted.Count - 1;
        int originalChar = -1;
        int formattedChar = -1;
        while ( true )
        {
            while ( originalChar < 0 && originalLine >= 0 )
            {
                originalChar = original[originalLine].Length - 1;
                if ( originalChar < 0 )
                {
                    originalLine--;
                }
            }

            while ( formattedChar < 0 && formattedLine >= 0 )
            {
                formattedChar = formatted[formattedLine].Length - 1;
                if ( formattedChar < 0 )
                {
                    formattedLine--;
                }
            }

            if ( originalLine < 0 || formattedLine < 0
                || original[originalLine][originalChar] != formatted[formattedLine][formattedChar] )
            {
                return shared;
            }

            shared++;
            originalChar--;
            formattedChar--;
            if ( originalChar < 0 )
            {
                originalLine--;
            }

            if ( formattedChar < 0 )
            {
                formattedLine--;
            }
        }
    }

    /// <summary>The first line whose whole content lies within the last <paramref name="tail"/> key characters.</summary>
    private static int FirstLineWithin(int[] fromEnd, IReadOnlyList<string> keys, int tail)
    {
        int line = 0;
        while ( line < keys.Count && fromEnd[line] > tail )
        {
            line++;
        }

        return line;
    }

    /// <summary>
    /// Pairs the lines of two regions spelling the same keys: a line pairs with the one covering
    /// exactly the same interval. Blank lines pair with blank lines at the same point; any other
    /// line whose interval does not match is left unpaired, and the lines up to where the intervals
    /// meet again become one edit.
    /// </summary>
    private static void MatchByInterval(
        IReadOnlyList<string> original, IReadOnlyList<string> formatted, int originalStart, int formattedStart, List<LinePair> pairs)
    {
        int i = originalStart;
        int j = formattedStart;
        while ( i < original.Count && j < formatted.Count )
        {
            bool originalBlank = original[i].Length == 0;
            bool formattedBlank = formatted[j].Length == 0;
            if ( originalBlank && formattedBlank )
            {
                pairs.Add(new LinePair(i, j));
                i++;
                j++;
                continue;
            }

            if ( originalBlank )
            {
                i++;
                continue;
            }

            if ( formattedBlank )
            {
                j++;
                continue;
            }

            if ( string.Equals(original[i], formatted[j], StringComparison.Ordinal) )
            {
                pairs.Add(new LinePair(i, j));
                i++;
                j++;
                continue;
            }

            // A split or a join: consume lines on whichever side is behind until both have covered
            // the same amount of content, and leave them all unpaired.
            int originalCovered = original[i].Length;
            int formattedCovered = formatted[j].Length;
            i++;
            j++;
            while ( originalCovered != formattedCovered )
            {
                if ( originalCovered < formattedCovered && i < original.Count )
                {
                    originalCovered += original[i].Length;
                    i++;
                }
                else if ( formattedCovered < originalCovered && j < formatted.Count )
                {
                    formattedCovered += formatted[j].Length;
                    j++;
                }
                else
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// The matched lines of <c>original[0, originalEnd)</c> and <c>formatted[0, formattedEnd)</c> by a
    /// Myers diff, or null past <see cref="MaxDifferences"/>.
    /// </summary>
    private static List<LinePair>? Myers(IReadOnlyList<string> original, IReadOnlyList<string> formatted, int n, int m)
    {
        int max = n + m;
        int offset = max + 1;
        int[] frontier = new int[2 * max + 3];

        // The frontier after each round, only the diagonals that round could reach, for walking back.
        List<int[]> trace = [];

        for ( int d = 0; d <= Math.Min(max, MaxDifferences); d++ )
        {
            for ( int k = -d; k <= d; k += 2 )
            {
                bool down = k == -d || (k != d && frontier[offset + k - 1] < frontier[offset + k + 1]);
                int x = down ? frontier[offset + k + 1] : frontier[offset + k - 1] + 1;
                int y = x - k;
                while ( x < n && y < m && string.Equals(original[x], formatted[y], StringComparison.Ordinal) )
                {
                    x++;
                    y++;
                }

                frontier[offset + k] = x;
                if ( x >= n && y >= m )
                {
                    trace.Add(Snapshot(frontier, offset, d));
                    return Backtrack(trace, n, m);
                }
            }

            trace.Add(Snapshot(frontier, offset, d));
        }

        return null;
    }

    private static int[] Snapshot(int[] frontier, int offset, int d)
    {
        int[] window = new int[2 * d + 1];
        Array.Copy(frontier, offset - d, window, 0, window.Length);
        return window;
    }

    /// <summary>Walks the recorded frontiers back from the end, collecting each diagonal run as pairs.</summary>
    private static List<LinePair> Backtrack(List<int[]> trace, int n, int m)
    {
        List<LinePair> pairs = [];
        int x = n;
        int y = m;
        for ( int d = trace.Count - 1; d > 0; d-- )
        {
            int[] previous = trace[d - 1];
            int k = x - y;
            bool down = k == -d || (k != d && At(previous, d - 1, k - 1) < At(previous, d - 1, k + 1));
            int previousK = down ? k + 1 : k - 1;
            int previousX = At(previous, d - 1, previousK);
            int previousY = previousX - previousK;

            // The run of matches this round slid along, back to where its one edit landed.
            int runStartX = down ? previousX : previousX + 1;
            while ( x > runStartX && y > runStartX - k )
            {
                x--;
                y--;
                pairs.Add(new LinePair(x, y));
            }

            x = previousX;
            y = previousY;
        }

        while ( x > 0 && y > 0 )
        {
            x--;
            y--;
            pairs.Add(new LinePair(x, y));
        }

        pairs.Reverse();
        return pairs;
    }

    private static int At(int[] window, int d, int k)
    {
        return window[k + d];
    }
}
