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
/// The preprocessor's complete output: the trivia-free parse stream (EndOfFile-terminated),
/// every macro visible at end of file, use sites, insert edges, the root-file regions
/// disabled by inactive #if branches (grey-out), and diagnostics.
/// </summary>
public sealed record PreprocessResult(
    ImmutableArray<PToken> Tokens,
    MacroTable Macros,
    ImmutableArray<MacroInvocation> MacroInvocations,
    ImmutableArray<InsertEdge> Inserts,
    ImmutableArray<TextRange> DisabledRegions,
    ImmutableArray<Diagnostic> Diagnostics);
