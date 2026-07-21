using System.Collections.Immutable;
using GSCode.Core.Docs;
using GSCode.Core.Text;

namespace GSCode.Core.Symbols;

// The extracted symbol surface of one script file. All records are fully populated —
// no nullable "not provided" members; empty collections and sentinels instead. The only
// modeled nullables are genuinely optional syntax (a parent class, a default value).

/// <summary>One declared parameter.</summary>
/// <param name="Name">Display-case name.</param>
/// <param name="ByRef">Declared with &amp; (array pass-by-reference).</param>
/// <param name="DefaultValueText">The default value as written, or "" when none.</param>
public sealed record ParameterSymbol(string Name, bool ByRef, string DefaultValueText);

/// <summary>One tracked assignment: a local (foo = x) or a field write (self.foo = x).</summary>
/// <param name="OwnerName">Lowercase owner: "" for locals, else self/level/game/world/anim or the variable's name.</param>
/// <param name="Name">Display-case variable/field name.</param>
/// <param name="KeyName">Lowercase-canonical name for lookups.</param>
/// <param name="Range">Root-file range of the assigned name.</param>
public sealed record AssignmentSymbol(string OwnerName, string Name, string KeyName, TextRange Range);

/// <summary>One declared function (top-level or class method).</summary>
public sealed record FunctionSymbol
{
    public required string Name { get; init; }

    /// <summary>Lowercase-canonical name (the SymbolKey name).</summary>
    public required string KeyName { get; init; }

    /// <summary>Lowercase namespace the function belongs to ("" for class methods — the class scopes them).</summary>
    public required string Namespace { get; init; }

    public bool IsPrivate { get; init; }
    public bool IsAutoexec { get; init; }

    /// <summary>
    /// Declared inside a <c>/# #/</c> dev block, so it does not exist in a release build.
    /// Callers outside a dev block are reported.
    /// </summary>
    public bool IsDevOnly { get; init; }
    public ImmutableArray<ParameterSymbol> Parameters { get; init; } = [];
    public bool HasVarargs { get; init; }

    /// <summary>Range of the name at the declaration (the go-to-definition target).</summary>
    public required TextRange NameRange { get; init; }

    /// <summary>Range of the whole declaration incl. body (root coordinates).</summary>
    public required TextRange FullRange { get; init; }

    /// <summary>File truly containing the declaration; "" = the record's own file.</summary>
    public string SourceFile { get; init; } = "";

    public ScriptDocComment Doc { get; init; } = ScriptDocComment.None;

    /// <summary>Every local/field assignment inside the body (the containment tree).</summary>
    public ImmutableArray<AssignmentSymbol> Assignments { get; init; } = [];
}

/// <summary>One class 'var' member.</summary>
public sealed record MemberSymbol(string Name, string KeyName, TextRange Range);

/// <summary>One declared class.</summary>
public sealed record ClassSymbol
{
    public required string Name { get; init; }
    public required string KeyName { get; init; }
    public required string Namespace { get; init; }

    /// <summary>Parent class name (lowercase), or null — single inheritance is optional syntax.</summary>
    public string? ParentKeyName { get; init; }

    public ImmutableArray<MemberSymbol> Members { get; init; } = [];
    public ImmutableArray<FunctionSymbol> Methods { get; init; } = [];
    public bool HasConstructor { get; init; }
    public bool HasDestructor { get; init; }

    public required TextRange NameRange { get; init; }
    public required TextRange FullRange { get; init; }
    public string SourceFile { get; init; } = "";
}

/// <summary>A #namespace region: the name and the root-file range it governs.</summary>
public sealed record NamespaceSpan(string Name, string KeyName, TextRange NameRange, TextRange GovernedRange);

/// <summary>How a reference site uses its symbol.</summary>
public enum ReferenceKind
{
    Definition,
    Call,
    AddressOf,
    ClassUse,
    FieldAccess,
    MacroUse,
    Literal,
}

/// <summary>One classified reference site: key + where + how. No text is stored beyond the interned key.</summary>
public readonly record struct ReferenceEntry(SymbolKey Key, TextRange Range, ReferenceKind Kind);
