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
/// - <c>#using</c>, <c>#include</c> and <c>#precache</c> are SORTED. An import is resolved by the
///   linker and a precache is a registration; neither can observe the other's position. The two
///   import spellings share a group because they are one idea per dialect, and where two imports
///   declare the same name the call is AMBIGUOUS rather than won by whichever came first — which
///   is what <c>AmbiguousFunctionLint</c> reports, and what makes sorting them safe.
/// - <c>#insert</c> and <c>#define</c> keep their relative order. An insert is textual — the file's
///   contents are spliced in where it sits — so two inserts can disagree about a macro, and a
///   define can be what an insert or a later define depends on.
/// - The whole pass BAILS if a <c>#define</c> appears before an <c>#insert</c>, which is the one
///   arrangement where regrouping could move an insert above a macro it needs. That does not occur
///   in any of the 980 stock scripts, but a mod is not the stock scripts.
/// - <c>#using_animtree</c> ENDS the block. It is not a preamble directive at all: it binds every
///   <c>%anim</c> reference below it until the next one, so its position is its meaning. The stock
///   scripts settle it — <c>util_shared.gsc</c> names <c>"generic"</c> at line 1530 and
///   <c>"all_player"</c> at 1551 and 1995, and <c>_civ_pickup.gsc</c> carries four, each sitting
///   directly above the function whose animations it binds, a thousand lines below any import.
///   Grouping it with <c>#using</c> hoisted it to the top of the file, which rebinds every
///   animation between the old position and the new one and cannot be seen in a diff of names.
///   The block ends rather than skipping over it, because a directive written BELOW one is below
///   it for a reason this pass has no way to check.
///
/// A directive continued with a trailing backslash owns every line of the continuation. That is
/// load-bearing for <c>#define</c>: the preprocessor ends a macro body at the first newline not
/// preceded by a backslash, so a blank line inserted between the <c>\</c> and the line it continues
/// empties the macro and turns its body into top-level code.
///
/// Comments travel with the directive beneath them, so an annotated import keeps its annotation —
/// except the run above the FIRST directive, which is a banner for the block and stays above it,
/// blank line or no blank line. Whatever spacing the author left between a banner and the block is
/// reproduced rather than normalised.
/// </summary>
public static class DirectiveSorter
{
    /// <summary>
    /// Canonical group order. Lower sorts first. A negative result ENDS the block, so the directive
    /// and everything below it is reproduced exactly.
    /// </summary>
    private static int GroupOf(string directive)
    {
        switch ( directive )
        {
            // The two spellings of an import, one per dialect (GameProfile.ImportStyle). Sorting
            // was a no-op on every Infinity Ward game until #include was here: the block ended at
            // the first directive in the file, so four of the five dialects got nothing.
            case "#using":
            case "#include":
                return 0;
            case "#insert":
                return 1;
            case "#namespace":
                return 2;
            case "#define":
                return 3;
            case "#precache":
                return 4;

            // Everything else, including #using_animtree — see the remark on the class.
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
        List<string> banner = [];
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
                if ( entries.Count == 0 && pending.Count > 0 )
                {
                    // A comment run above the block, ended by the author's own blank line. The
                    // blank is carried into the banner rather than re-added at the end, so a file
                    // header and a section header below it keep the gap the author put between
                    // them, and a banner that hugged the block still hugs it.
                    banner.AddRange(pending);
                    banner.Add("");
                    pending.Clear();
                }

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

            // Comments above the FIRST directive describe the BLOCK, not the import they happen to
            // touch, so they stay above it rather than travelling with whichever directive sorting
            // puts first. Whether a blank line separated them is not the signal it looks like: of
            // the fourteen files across the BO3 and CoD4 corpora with a comment run hugging their
            // first import, all fourteen are headers — `// COMMON AI SYSTEMS INCLUDES`,
            // `// ARCHETYPE UTILITY SCRIPTS`, and a ruled banner in `_siegebot.gsc` whose text is
            // literally `#using`. None annotates the one import beneath it. Carrying those into
            // the middle of the block is what this rule exists to stop.
            List<string> owned = entries.Count == 0 ? [line] : [.. pending, line];
            if ( entries.Count == 0 )
            {
                banner.AddRange(pending);
            }

            pending.Clear();

            // A trailing backslash binds the next PHYSICAL line, so the whole run is one entry.
            // Leaving the continuation to the remainder puts this pass's blank separator between a
            // '\' and the line it continues, which ends a macro body.
            while ( index + 1 < lines.Length && IsContinued(lines[index]) )
            {
                index++;
                owned.Add(lines[index]);
            }

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
        foreach ( string line in banner )
        {
            rebuilt.Append(line).Append('\n');
        }

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

    /// <summary>Whether a line ends in a backslash, so the next physical line continues it.</summary>
    private static bool IsContinued(string line)
    {
        string trimmed = line.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] == '\\';
    }

    /// <summary>
    /// The safety gate: reordering may move lines but must never change the set of them. Blank
    /// lines are excluded, since rewriting the separators is the point.
    ///
    /// Except after a backslash. There a blank line is not a separator but a semantic edit — it
    /// ends the macro the backslash was continuing — so a continued line is compared JOINED to
    /// what physically follows it, blank or not. Without that, the one whitespace change this pass
    /// can make that alters meaning is the one change the gate is blind to.
    /// </summary>
    internal static bool SameLines(string before, string after)
    {
        List<string> left = LogicalLines(before);
        List<string> right = LogicalLines(after);

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

    /// <summary>Trimmed non-blank lines, with a backslash-continued run folded into one entry.</summary>
    private static List<string> LogicalLines(string text)
    {
        string[] lines = text.Split('\n');
        List<string> logical = [];

        for ( int index = 0; index < lines.Length; index++ )
        {
            string trimmed = lines[index].Trim();
            if ( trimmed.Length == 0 )
            {
                continue;
            }

            StringBuilder joined = new(trimmed);
            while ( index + 1 < lines.Length && joined[^1] == '\\' )
            {
                index++;
                joined.Append('\n').Append(lines[index].Trim());
            }

            logical.Add(joined.ToString());
        }

        return logical;
    }
}
