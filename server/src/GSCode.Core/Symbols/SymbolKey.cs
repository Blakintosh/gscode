namespace GSCode.Core.Symbols;

/// <summary>What a symbol key identifies.</summary>
public enum SymbolKind
{
    Function,
    Class,
    Macro,
    Field,
    StringLiteral,
    HashString,
    LocalizedString,
    AnimReference,

    /// <summary>
    /// A class <c>var</c> — <c>var _b_set_goal;</c> and the bare <c>_b_set_goal</c> that reads it.
    ///
    /// Distinct from <see cref="Field"/>, which it otherwise resembles, because a member IS
    /// declared: it has one <see cref="ReferenceKind.Definition"/> at the <c>var</c>, where a
    /// field has none anywhere and has to be answered from its writes.
    ///
    /// Read as a BARE NAME inside the class body, never through <c>self.</c> — all 206 <c>var</c>
    /// declarations in BO3's shipped scripts are used that way. So the thing at the cursor looks
    /// exactly like a local, and only the enclosing class says otherwise.
    ///
    /// Appended rather than slotted beside <see cref="Field"/> on purpose: a kind travels to the
    /// record cache as its ordinal, and inserting one mid-enum costs a format bump and misreads
    /// every older blob. The four literal kinds above have that hazard and no reason to move.
    /// </summary>
    Member,
}

/// <summary>
/// The cross-file lookup key. Namespace and Name are lowercase-canonical interned
/// strings (macros and string literals keep exact case — their kinds are the two
/// case-sensitive spaces). Namespace is null for builtins, macros, fields, and literals.
/// Language is NOT part of the key: GSC/CSC isolation is structural (separate stores).
///
/// A CLASS METHOD is a <see cref="SymbolKind.Function"/> with a non-null
/// <see cref="OwnerClass"/> and a null Namespace — the class scopes it, so it has no namespace of
/// its own. Deliberately not a separate kind: every handler that gates on
/// <c>Kind == SymbolKind.Function</c> should treat a method as a function, and a new kind would have
/// made each of those a silent omission instead.
/// </summary>
/// <param name="OwnerClass">
/// The class that scopes this name, lowercase-interned, or null when nothing does.
///
/// Set for a method DECLARATION and for the call forms that carry no written qualifier — a bare call
/// inside a class body, and <c>[[self]]-&gt;m()</c> — because there the class is genuinely part of
/// what the name means. It is NOT set for a written <c>A::b()</c>, even inside a class: there the
/// qualifier is the identity, and keying the enclosing class as well would separate the call from
/// the definition it names. A dialect can also declare a namespace and a class with the SAME name
/// (BO3's <c>phalanx.gsc</c> and <c>throttle_shared.gsc</c> both do) and resolve <c>A::b()</c> to
/// the namespace, so the written form must key exactly as it would outside a class.
///
/// Where a rule needs the ENCLOSING class of a written-qualifier call, recover it positionally from
/// the file's own <c>ClassSymbol.FullRange</c> — it is a property of the call site, not of the
/// symbol being called.
/// </param>
public readonly record struct SymbolKey(string? Namespace, string Name, SymbolKind Kind, string? OwnerClass = null);

/// <summary>
/// The written qualifier that names the engine's own library: <c>sys::name()</c>.
///
/// Builtins are namespace-less, so a call written this way is keyed with a NULL namespace — the key
/// a builtin reached by a bare name carries — and never as a script namespace called <c>sys</c>. One
/// spelling, because every reader of a written qualifier has to agree on it: a site that compares
/// its own literal, or forgets to, reads <c>sys</c> as a namespace nothing declares into, and the
/// call resolves to nothing, or to whichever script function shares the name.
/// </summary>
public static class BuiltinQualifier
{
    public const string Text = "sys";

    /// <summary>
    /// Whether a WRITTEN qualifier names the engine's library in this game: <c>sys</c>, in any case,
    /// on a namespace dialect. Nowhere else — no shipped CoD4 or BO1 script writes <c>sys::</c>, and
    /// on a merge dialect it keys like any other qualifier.
    ///
    /// The one test for readers holding TEXT: extraction, the argument-count lint, completion and
    /// signature help, the last two because <c>sys::</c> with nothing after it is not yet a call.
    /// There is deliberately no ungated form, so no reader can agree on the spelling and disagree on
    /// the dialect.
    /// </summary>
    public static bool Matches(string qualifier, GameProfile game)
    {
        return game.ResolvesByNamespace && string.Equals(qualifier, Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a key is the one a <c>sys::</c> call is keyed under: a function with no namespace and
    /// no owning class, on a namespace dialect. Nothing else produces that key there — an unqualified
    /// call is keyed under the file's own namespace and a method carries its class — so no script
    /// declaration can answer it. On a merge dialect every unqualified call has this shape, so there
    /// it proves nothing and the answer is false.
    ///
    /// The one test for readers holding a KEY. Readers holding only text — completion and signature
    /// help, where <c>sys::</c> with nothing after it is not yet a call — ask <see cref="Matches"/>.
    /// </summary>
    public static bool IsBuiltinKey(SymbolKey key, GameProfile game)
    {
        return key.Kind == SymbolKind.Function
            && key.Namespace is null
            && key.OwnerClass is null
            && game.ResolvesByNamespace;
    }
}
