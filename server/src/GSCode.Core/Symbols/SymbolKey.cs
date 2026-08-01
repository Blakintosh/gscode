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
public readonly record struct SymbolKey(string? Namespace, string Name, SymbolKind Kind, string? OwnerClass = null)
{
    /// <summary>Whether a class scopes this name — i.e. it is a class method rather than a function.</summary>
    public bool IsMethod
    {
        get { return OwnerClass is not null; }
    }
}
