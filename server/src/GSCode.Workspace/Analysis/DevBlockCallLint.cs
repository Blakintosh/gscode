using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a call to a function declared inside a <c>/# #/</c> dev block from code that is not
/// itself in one. Dev blocks are stripped from a release build, so the call compiles and runs
/// fine while developing and then fails only once the mod ships — exactly the kind of bug worth
/// catching early.
///
/// The two halves come from different places on purpose. The CALLEE's dev-ness is a stored
/// fact (<see cref="FunctionSymbol.IsDevOnly"/>), so the check works across files. The CALLER's
/// is computed live from the asking file's own tree, which costs nothing to store and stays
/// correct for unsaved edits.
///
/// Engine builtins get the same treatment through <see cref="BuiltinFunction.IsDevOnly"/>,
/// since some exist only in a development build. There is no declaration to point at for
/// those, so they are reported without related information.
/// </summary>
public static class DevBlockCallLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        string askingContextId,
        string askingPath,
        ImmutableArray<string> askingNamespaces,
        BuiltinApi builtins)
    {
        ImmutableArray<TextRange> devRegions = DevRegions(result);
        FunctionLookupCache lookups = new(store, askingContextId, askingPath, askingNamespaces);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            // A call that is itself dev-only disappears alongside its target, so it is fine.
            if ( IsInsideDevRegion(entry.Range, devRegions) )
            {
                continue;
            }

            ImmutableArray<ResolvedFunction> resolved = lookups.Lookup(entry.Key.Namespace, entry.Key.Name);

            if ( resolved.Length == 0 )
            {
                // No script function by that name, so it may be an engine builtin. Some of those
                // exist only in a development build and are just as broken to call from release
                // code, but the engine owns them, so there is no declaration to point at. The
                // flag is read off the function itself, so whether it came from the curated list
                // or one day from the API data makes no difference here.
                BuiltinFunction? builtin = builtins.Find(entry.Key.Name);
                if ( builtin is not null && builtin.IsDevOnly )
                {
                    // The API's display casing, not the lowercase lookup key, so the message
                    // reads "PrintLn" the way the author wrote it.
                    diagnostics.Add(Diagnostic.Create(
                        entry.Range,
                        DiagnosticSeverity.Error,
                        GscDiagnosticCode.DevOnlyFunctionCalledFromRelease,
                        builtin.Name));
                }

                continue;
            }

            // Only report when every candidate is dev-only: if any visible overload survives a
            // release build, the call is fine.
            if ( !AllDevOnly(resolved) )
            {
                continue;
            }

            Diagnostic diagnostic = Diagnostic.Create(
                entry.Range,
                DiagnosticSeverity.Error,
                GscDiagnosticCode.DevOnlyFunctionCalledFromRelease,
                resolved[0].Function.Name);

            DiagnosticRelation declaredAt = new(
                resolved[0].Record.Path, resolved[0].Function.NameRange, "Declared inside a dev block here.");

            diagnostics.Add(diagnostic with { RelatedInformation = [declaredAt] });
        }

        return diagnostics.ToImmutable();
    }

    private static bool AllDevOnly(ImmutableArray<ResolvedFunction> resolved)
    {
        foreach ( ResolvedFunction candidate in resolved )
        {
            if ( !candidate.Function.IsDevOnly )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Every <c>/# #/</c> span in the file, declaration- and statement-level alike. Collected
    /// as ranges rather than tracked during extraction so nothing extra is stored per reference,
    /// which matters because the reference list is the largest part of a record.
    /// </summary>
    private static ImmutableArray<TextRange> DevRegions(ParseResult result)
    {
        ImmutableArray<TextRange>.Builder regions = ImmutableArray.CreateBuilder<TextRange>();
        Collect(result.Tree.Root, regions);

        return regions.ToImmutable();
    }

    private static void Collect(AstNode node, ImmutableArray<TextRange>.Builder regions)
    {
        if ( node is DevBlockDeclNode or DevBlockStmtNode )
        {
            // Nested blocks add nothing: the outer range already covers them.
            regions.Add(node.Range);
            return;
        }

        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            Collect(child, regions);
        }
    }

    private static bool IsInsideDevRegion(TextRange range, ImmutableArray<TextRange> devRegions)
    {
        foreach ( TextRange region in devRegions )
        {
            if ( region.Contains(range.Start) )
            {
                return true;
            }
        }

        return false;
    }
}
