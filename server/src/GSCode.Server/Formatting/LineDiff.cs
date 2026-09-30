namespace GSCode.Server.Formatting;

/// <summary>An original line and the formatted line it became.</summary>
internal readonly record struct LinePair(int Original, int Formatted);

/// <summary>
/// Which original lines correspond to which formatted lines, found by a Myers diff over each line's
/// CONTENT KEY — its text with every whitespace character removed and lowercased.
/// </summary>
/// <remarks>
/// The formatter changes whitespace and, with fixCasing, letter case; it never changes what a line
/// says. So compared by key, a formatted file lines up with its original almost line for line, and
/// the only differences left are the lines the formatter added, removed or split. That keeps the diff
/// small however much of the file was reindented — a Myers diff costs time in proportion to the
/// number of differences, not to the file's length squared, which is what the previous line diff
/// cost, and why it gave up on anything over 3,000 changed lines and returned one edit for all of
/// it. A matched pair whose text differs becomes a one-line edit of its own, so every change stays
/// where it is and an editor's caret on any other line does not move.
/// </remarks>
internal static class LineDiff
{
    /// <summary>
    /// Past this many insertions and deletions the answer is abandoned. The formatter's own output
    /// never comes near it; only a caller diffing unrelated texts would.
    /// </summary>
    private const int MaxDifferences = 4000;

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

    /// <summary>
    /// The matched lines, in order, or null when the texts differ by more than
    /// <see cref="MaxDifferences"/> insertions and deletions.
    /// </summary>
    public static List<LinePair>? Match(IReadOnlyList<string> original, IReadOnlyList<string> formatted)
    {
        int n = original.Count;
        int m = formatted.Count;
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
