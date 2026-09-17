using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;

namespace GSCode.Parser.Preprocessing;

/// <summary>One #insert edge, resolved or not — the dependency tracker consumes these.</summary>
/// <param name="RawPath">The path exactly as written after #insert.</param>
/// <param name="ResolvedPath">Normalized absolute path, or null when resolution failed.</param>
/// <param name="DirectiveRange">Range of the PATH ARGUMENT, not the whole directive — rename
/// rewriting replaces exactly this span, leaving the keyword and semicolon alone.</param>
/// <param name="ContainingFile">File holding the directive; null = the root file.</param>
public sealed record InsertEdge(string RawPath, string? ResolvedPath, TextRange DirectiveRange, string? ContainingFile);

/// <summary>One macro use site — powers find-references, hover, and signature help for macros.</summary>
/// <param name="Name">The macro's exact-case name.</param>
/// <param name="SourceFile">File containing the use; null = the root file.</param>
/// <param name="Range">Range of the name token at the use site.</param>
/// <param name="Definition">The definition that was expanded.</param>
public sealed record MacroInvocation(string Name, string? SourceFile, TextRange Range, MacroDefinition Definition);

/// <summary>
/// One use of a predefined macro (<c>__FUNCTION__</c>, <c>__FILE__</c>, <c>__LINE__</c>) and what it
/// expanded to. By the time extraction runs, the token these produced is an ordinary literal — a
/// String or an Integer — with nothing left marking where it came from, so hover has nowhere else to
/// read the resolved value back from. <see cref="Range"/> is root-file coordinates (the token's own
/// <c>RootRange</c>), matching how a literal reference is keyed, so a hover position can be matched
/// against this list directly.
/// </summary>
public sealed record BuiltinExpansion(string Name, TextRange Range, string ExpandedText);

/// <summary>
/// The preprocessor's complete output: the trivia-free parse stream (EndOfFile-terminated),
/// every macro visible at end of file, every <c>#define</c> site seen (win or lose), use
/// sites, insert edges, the root-file regions disabled by inactive #if branches (grey-out),
/// and diagnostics.
/// </summary>
/// <param name="Macros">The SURVIVING definition per name — last one wins, matching engine
/// behavior. Not every definition the file wrote: a name defined twice keeps only the second.</param>
/// <param name="AllMacroDefinitions">Every <c>#define</c> actually parsed, in source order,
/// including one a later redefinition went on to shadow. <see cref="Macros"/> answers "what does
/// this name resolve to"; this answers "what did the file declare" — the question a reference at
/// each <c>#define</c>'s own name deserves regardless of which one the table kept. A shadowed
/// root-file definition (redefined later in the same file, or overridden by a later `#insert`)
/// used to have NO Definition reference at all: <c>Macros.All</c> holds only the winner, so the
/// loser's own name was invisible to go-to-definition and rename.</param>
public sealed record PreprocessResult(
    ImmutableArray<PToken> Tokens,
    MacroTable Macros,
    ImmutableArray<MacroDefinition> AllMacroDefinitions,
    ImmutableArray<MacroInvocation> MacroInvocations,
    ImmutableArray<BuiltinExpansion> BuiltinExpansions,
    ImmutableArray<InsertEdge> Inserts,
    ImmutableArray<TextRange> DisabledRegions,
    ImmutableArray<Diagnostic> Diagnostics);
