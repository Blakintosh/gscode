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
/// </summary>
public readonly record struct SymbolKey(string? Namespace, string Name, SymbolKind Kind);
