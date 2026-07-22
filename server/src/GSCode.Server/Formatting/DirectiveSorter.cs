using System.Text;

namespace GSCode.Server.Formatting;

/// <summary>
/// Groups and sorts the directive block at the top of a file.
///
/// This is the formatter's one operation that moves code rather than whitespace, so it is fenced
/// carefully. It runs as a post-pass on already-reflowed text, AFTER the token-stream equality
/// gate — that gate exists to prove the reflow changed nothing but spacing, and reordering would
/// trip it by design. In its place this pass proves its own safety: the multiset of lines it emits
/// must equal the multiset it consumed, so a line can be moved but never dropped, duplicated or
/// edited.
///
/// Ordering rules, and why they differ per kind:
///
/// - <c>#using</c> and <c>#precache</c> are SORTED. A using is a namespace import resolved by the
///   linker and a precache is a registration; neither can observe the other's position.
/// - <c>#insert</c> and <c>#define</c> keep their relative order. An insert is textual — the file's
///   contents are spliced in where it sits — so two inserts can disagree about a macro, and a
///   define can be what an insert or a later define depends on.
/// - The whole pass BAILS if a <c>#define</c> appears before an <c>#insert</c>, which is the one
///   arrangement where regrouping could move an insert above a macro it needs. That does not occur
///   in any of the 980 stock scripts, but a mod is not the stock scripts.
///
/// Comments travel with the directive beneath them, so an annotated import keeps its annotation.
/// </summary>
public static class DirectiveSorter
{
    /// <summary>Canonical group order. Lower sorts first.</summary>
    private static int GroupOf(string directive)
    {
        switch ( directive )
        {
            case "#using":
            case "#using_animtree":
                return 0;
            case "#insert":
                return 1;
            case "#namespace":
            case "#animtree":
                return 2;
            case "#define":
                return 3;
            case "#precache":
                return 4;
            default:
                return -1;
        }
    }

    /// <summary>Whether a group's members can be reordered among themselves.</summary>
    private static bool IsSortable(int group)
    {
        return group == 0 || group == 4;
    }

    private sealed class Entry
    {
        public required int Group { get; init; }

        /// <summary>Position in the input, so a non-sortable group can be restored exactly.</summary>
        public required int Ordinal { get; init; }

        /// <summary>The directive line itself, plus any comment lines that sat above it.</summary>
        public required List<string> Lines { get; init; }

        public required string SortKey { get; init; }
    }

    /// <summary>
    /// Returns the text with its leading directive block grouped and sorted, or null when there is
    /// nothing to do or it is not safe to do it.
    /// </summary>
    public static string? Sort(string formatted)
    {
        string[] lines = formatted.Split('\n');

        List<Entry> entries = [];
        List<string> pending = [];
        int consumedThrough = -1;
        int ordinal = 0;
        bool sawDefine = false;

        for ( int index = 0; index < lines.Length; index++ )
        {
            string line = lines[index];
            string trimmed = line.Trim();

            if ( trimmed.Length == 0 )
            {
                // Blank lines inside the block are the separators this pass rewrites; drop them.
                // A blank line only ends the block if code has already followed, which the
                // directive/comment checks below decide.
                continue;
            }

            if ( trimmed.StartsWith("//", StringComparison.Ordinal) )
            {
                pending.Add(line);
                continue;
            }

            string directive = DirectiveOf(trimmed);
            int group = GroupOf(directive);
            if ( group < 0 )
            {
                // First thing that is not a directive: the block is over.
                break;
            }

            if ( directive == "#define" )
            {
                sawDefine = true;
            }
            else if ( directive == "#insert" && sawDefine )
            {
                // Regrouping would lift this insert above a macro that precedes it today.
                return null;
            }

            List<string> owned = [.. pending, line];
            pending.Clear();

            entries.Add(new Entry
            {
                Group = group,
                Ordinal = ordinal++,
                Lines = owned,
                SortKey = trimmed,
            });

            consumedThrough = index;
        }

        if ( entries.Count == 0 || consumedThrough < 0 )
        {
            return null;
        }

        // Comments trailing the block belong to whatever follows it, not to the last directive.
        List<Entry> ordered = [.. entries];
        ordered.Sort(static (left, right) =>
        {
            if ( left.Group != right.Group )
            {
                return left.Group.CompareTo(right.Group);
            }

            if ( !IsSortable(left.Group) )
            {
                return left.Ordinal.CompareTo(right.Ordinal);
            }

            int byText = string.Compare(left.SortKey, right.SortKey, StringComparison.OrdinalIgnoreCase);
            return byText != 0 ? byText : left.Ordinal.CompareTo(right.Ordinal);
        });

        StringBuilder rebuilt = new();
        int previousGroup = -1;
        foreach ( Entry entry in ordered )
        {
            if ( previousGroup >= 0 && entry.Group != previousGroup )
            {
                rebuilt.Append('\n');
            }

            foreach ( string line in entry.Lines )
            {
                rebuilt.Append(line).Append('\n');
            }

            previousGroup = entry.Group;
        }

        // Whatever came after the block, with exactly one blank line before it.
        string remainder = string.Join('\n', lines.Skip(consumedThrough + 1)).TrimStart('\n');
        if ( remainder.Length > 0 )
        {
            rebuilt.Append('\n').Append(remainder);
        }

        string result = rebuilt.ToString();
        return SameLines(formatted, result) && result != formatted ? result : null;
    }

    private static string DirectiveOf(string trimmed)
    {
        int end = 0;
        while ( end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '(' )
        {
            end++;
        }

        return trimmed[..end];
    }

    /// <summary>
    /// The safety gate: reordering may move lines but must never change the set of them. Blank
    /// lines are excluded, since rewriting the separators is the point.
    /// </summary>
    private static bool SameLines(string before, string after)
    {
        List<string> left = [.. before.Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0)];
        List<string> right = [.. after.Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0)];

        if ( left.Count != right.Count )
        {
            return false;
        }

        left.Sort(StringComparer.Ordinal);
        right.Sort(StringComparer.Ordinal);

        for ( int index = 0; index < left.Count; index++ )
        {
            if ( !string.Equals(left[index], right[index], StringComparison.Ordinal) )
            {
                return false;
            }
        }

        return true;
    }
}
