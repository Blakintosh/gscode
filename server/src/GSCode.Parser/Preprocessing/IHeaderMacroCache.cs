using System.Collections.Immutable;

namespace GSCode.Parser.Preprocessing;

/// <summary>
/// What one <c>#insert</c>ed header contributes, once computed: the macros it defines, in the order
/// it defines them, and the insert edges it carries from its own nested inserts.
/// </summary>
/// <remarks>
/// Order is kept because a later <c>#define</c> of a name wins. Replaying the list in order
/// reproduces exactly what a linear walk of the header would have left in the table, including a
/// redefinition shadowing an earlier one.
/// </remarks>
public sealed record HeaderContribution(
    ImmutableArray<MacroDefinition> Definitions,
    ImmutableArray<InsertEdge> Inserts);

/// <summary>
/// Remembers what a header contributes so that the second file to insert it need not walk it again.
///
/// A header's contribution is fixed by the header alone: BO3's carry no conditional compilation at
/// all (checked - zero <c>#if</c> lines across all 118), so nothing about the including file can
/// change what they define. Without this, the preprocessor re-walks every inserted header and
/// re-registers every <c>#define</c> in it once per including file: BO3 has 2,137 insert directives
/// naming 114 distinct headers, so each is processed about nineteen times, and preprocessing is 60%
/// of its analysis against CoD4's 10%.
///
/// The interface lives here, beside <see cref="IInsertProvider"/>, because the parser cannot
/// reference the workspace that owns the cache instance.
/// </summary>
public interface IHeaderMacroCache
{
    bool TryGet(string resolvedPath, out HeaderContribution contribution);

    void Store(string resolvedPath, HeaderContribution contribution);
}
