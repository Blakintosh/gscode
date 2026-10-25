using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The Infinity Ward counterpart to <see cref="UnusedUsingLint"/>: an <c>#include</c> whose target
/// contributes nothing this file uses. Reported as a Hint tagged Unnecessary so the directive greys
/// out.
///
/// <c>#include</c> MERGES a file's OWN functions into this scope, and nothing it includes in turn,
/// so "used" is by NAME, against
/// what the target declares itself. A bare call is the only use: a path call loads the file it names
/// on its own, so an include that only path calls go into adds nothing. Deliberately conservative
/// otherwise — an autoexec in the target keeps it (imported for its side effects).
///
/// An unreadable <c>#include</c> DIRECTIVE does not suppress the whole pass, for the reason
/// <see cref="UnusedUsingLint"/> sets out: it never enters the resolved list, so it goes unjudged
/// while its siblings are judged.
///
/// There is no unresolved-<c>#insert</c> gate here, unlike <see cref="UnusedUsingLint"/>, and that
/// is a dialect fact rather than an oversight: <c>#insert</c> does not lex on an include dialect
/// (<c>Keywords.IsDirectiveEnabled</c>), so the diagnostic that gate reads can never be raised in a
/// file this rule runs on.
/// </summary>
public static class UnusedIncludeLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        FileImports? imports = null)
    {
        // Resolved once per file by WorkspaceLints and shared with the other import lints; falling
        // back to resolving here keeps this callable on its own, which the tests rely on.
        FileImports resolvedImports = imports ?? FileImports.Resolve(result, store, language, resolver, askingPath);

        if ( resolvedImports.Includes.Length == 0 )
        {
            return [];
        }

        // Every function name this file calls BARE, ignoring its own definitions. Merge dialects key
        // functions with no namespace, so the name is the whole key; a path call is told apart by its
        // range and does not count.
        HashSet<TextRange> pathCallRanges = PathCallReference.NameRanges(result.Extraction.PathCalls);

        HashSet<string> calledFunctions = new(StringComparer.Ordinal);
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // Macro-expanded uses count, for the reason UnusedUsingLint spells out — including
            // the declaration-shaped ones, which is what the flag test preserves.
            if ( (entry.Kind != ReferenceKind.Definition || entry.FromMacro)
                && entry.Key.Kind == SymbolKind.Function
                && !pathCallRanges.Contains(entry.Range) )
            {
                calledFunctions.Add(entry.Key.Name);
            }
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( ImportedFile imported in resolvedImports.Includes )
        {
            if ( DeclaresSomethingWanted(imported.Record, calledFunctions) )
            {
                continue;
            }

            Diagnostic unused = Diagnostic.Create(
                imported.DirectiveRange, DiagnosticSeverity.Hint, GscDiagnosticCode.UnusedInclude, imported.RawPath);

            diagnostics.Add(unused with { Tags = [DiagnosticTag.Unnecessary] });
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>Whether the target itself declares something this file calls, or runs on import.</summary>
    private static bool DeclaresSomethingWanted(ScriptRecord record, HashSet<string> calledFunctions)
    {
        foreach ( FunctionSymbol function in record.Functions )
        {
            if ( function.IsAutoexec || calledFunctions.Contains(function.KeyName) )
            {
                return true;
            }
        }

        return false;
    }
}
