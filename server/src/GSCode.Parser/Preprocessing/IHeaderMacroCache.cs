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
/// Only for headers whose contribution is fixed by the header alone. That is not a property of
/// headers in general, so the preprocessor establishes it per header and refuses to store anything
/// else: a header that emitted tokens, reported a diagnostic, invoked a macro or evaluated a
/// <c>#if</c> is walked again for every file that inserts it. A conditional is the subtle one — a
/// condition naming a macro the including file did NOT define expands to nothing, silently takes
/// the <c>#else</c>, and leaves no invocation behind to notice. BO3's stock headers carry no
/// conditional compilation at all (checked - zero <c>#if</c> lines across all 118), which is why
/// refusing them costs nothing there.
///
/// Nested <c>#insert</c>s are the other half: the key is the outer header's resolved path, but the
/// stored edges were resolved through whichever file walked it first, and resolution is
/// per-context. The preprocessor re-resolves them on a hit and walks the header instead when any
/// lands elsewhere, so an entry can be replayed only in the world that produced it.
///
/// Without this, the preprocessor re-walks every inserted header and re-registers every
/// <c>#define</c> in it once per including file: BO3 has 2,137 insert directives naming 114 distinct
/// headers, so each is processed about nineteen times, and preprocessing is 60% of its analysis
/// against CoD4's 10%.
///
/// The interface lives here, beside <see cref="IInsertProvider"/>, because the parser cannot
/// reference the workspace that owns the cache instance.
/// </summary>
public interface IHeaderMacroCache
{
    bool TryGet(string resolvedPath, out HeaderContribution contribution);

    void Store(string resolvedPath, HeaderContribution contribution);

    /// <summary>
    /// A counter that moves whenever any header held here changes, so a completed analysis can be
    /// asked whether the headers it expanded are still the ones on disk.
    ///
    /// A parse is stale for two reasons, not one: the file's own text moved, or a header it
    /// <c>#insert</c>s did. Only the first was ever checked, so editing a GSH left every open
    /// dependent holding a parse of the OLD macro bodies while reporting itself current — the
    /// reported hover bug, where a value updated only once something was typed into the dependent.
    ///
    /// One counter for all headers rather than a per-document dependency set: a header edit is a
    /// rare, user-paced event and open documents are few, so the cost of the occasional needless
    /// re-parse is far below the cost of getting a dependency set subtly wrong — nested inserts
    /// make "which files see this header" a transitive question, and a narrow answer would be
    /// silently stale rather than merely conservative.
    ///
    /// The parser does not read this. The interface lives here for the same reason the rest of it
    /// does: the parser cannot reference the workspace that owns the cache.
    /// </summary>
    long Generation { get; }
}
