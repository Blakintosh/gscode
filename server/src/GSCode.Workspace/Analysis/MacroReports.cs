using GSCode.Core.Symbols;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The one rule six lints share about reporting on a reference a macro expanded into: say it once
/// per site, and pay nothing when no macro is involved.
///
/// A macro-expanded reference is keyed to the INVOCATION, so a body naming three things puts three
/// entries on one range and a rule looping over them stacks three findings on one word. Text the
/// author wrote cannot do that — a range is one name token and one token is one reference — so the
/// set holds expanded entries alone and is not allocated until one arrives. Every file in a dialect
/// without a preprocessor therefore pays nothing, and so does every BO3 file invoking no macro.
///
/// The set is the CALLER'S local, passed by <c>ref</c>, rather than state held here. Six lints each
/// wrote this block out and the obvious collapse was a small struct owning the set — which is a
/// mutable struct, and copying one before its first insert would give each copy its own set and
/// split the deduplication silently. A <c>ref</c> to the caller's own local cannot be copied wrong.
///
/// WHAT the key is stays the caller's, because it is the rule's own claim rather than a detail:
/// 5000 keys on the namespace it would have you import, 5026 on the function name it would have you
/// include, and the resolution lint on the whole symbol plus its kind. Deduplicating on the range
/// alone would be wrong for every one of them — two namespaces missing at one invocation are two
/// imports to add.
/// </summary>
internal static class MacroReports
{
    /// <summary>
    /// Whether this entry should be reported on, given what has already been said at its range.
    /// Always true for text the author wrote; true once per distinct <paramref name="key"/> for
    /// text a macro expanded into.
    /// </summary>
    public static bool ShouldReport<TKey>(ReferenceEntry entry, TKey key, ref HashSet<TKey>? reported)
    {
        if ( !entry.FromMacro )
        {
            return true;
        }

        reported ??= [];

        return reported.Add(key);
    }
}
