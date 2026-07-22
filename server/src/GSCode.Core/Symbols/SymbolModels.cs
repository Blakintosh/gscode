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

    /// <summary>
    /// A string literal that is an operand of <c>+</c> — a fragment of a message being built up,
    /// rather than a name.
    ///
    /// Still a reference, so find-all-references on the text still finds it. The kind exists only
    /// so literal COMPLETION can leave it out: offering these filled the list with things like
    /// "already exists. Proceeding with override" and " at origin: ", half-sentences nobody wants
    /// to insert.
    ///
    /// This is a STRUCTURAL rule — what the code does with the string — and two tempting textual
    /// ones were measured against the stock scripts and rejected:
    ///
    /// * "drop anything containing a space" loses 54 real notify/endon events, among them
    ///   "abort forfeit", "missile fired", "stop sound" and "destination reached". BO3 event names
    ///   are often multi-word, and it would be wrong for localized strings besides.
    /// * "drop anything appearing in only one file" removes 73% of all literals, including
    ///   single-use asset and model names, and would hide a name from the second file that uses
    ///   it — which is exactly when it is wanted.
    ///
    /// After this rule only about 5% of the remaining literals even contain a space, so the noise
    /// it was chasing is largely gone.
    /// </summary>
    ConcatenatedLiteral,

    /// <summary>
    /// A use that came from inside a MACRO BODY, recorded against the invocation site in this
    /// file rather than the macro's own text.
    ///
    /// These used to be dropped outright, which was right about ranges and wrong about facts.
    /// `REGISTER_SYSTEM(...)` expands to `system::register(...)`, so a file using that macro
    /// really does call into the `system` namespace — but with nothing recorded, the unused-import
    /// lint saw no use and told 471 stock files their `#using scripts\shared\system_shared` was
    /// pointless. Code-lens counts and find-all-references were short by the same amount.
    ///
    /// Kept as a distinct kind rather than folded in, because the two consumers want opposite
    /// things: counting a use is right, but resolving the CURSOR to one is not — the text under
    /// it reads `REGISTER_SYSTEM`, and go-to-definition there must still reach the macro.
    /// </summary>
    ExpandedFromMacro,
}

/// <summary>One classified reference site: key + where + how. No text is stored beyond the interned key.</summary>
public readonly record struct ReferenceEntry(SymbolKey Key, TextRange Range, ReferenceKind Kind);
