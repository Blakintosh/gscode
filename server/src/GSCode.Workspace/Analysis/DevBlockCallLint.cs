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
///
/// Resolution goes through <see cref="MethodResolution.ResolveCall"/> rather than straight to
/// <see cref="DatabaseQueries.LookupFunctions"/>, because a bare call inside a class body means a
/// METHOD first and that query cannot see one. A dev-only method is worth catching for exactly the
/// same reason a dev-only function is, and routing is what makes it visible here.
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

        // Where a bare call inside a class falls back TO when no class in the chain declares the
        // name: the file's own namespace function, which is what the call then really means. Same
        // source as FunctionResolutionLint uses for the same purpose.
        string fileNamespace = askingNamespaces.IsDefaultOrEmpty ? "" : askingNamespaces[0];

        // Keyed on the WRITTEN key, so a name called repeatedly in one file is routed once. This
        // stands in for the FunctionLookupCache that used to be here: that memo can only ask
        // LookupFunctions, and LookupFunctions is the query this rule must no longer use alone.
        Dictionary<SymbolKey, ImmutableArray<ResolvedFunction>> resolutions = [];

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // Keyed on the symbol: one dev-only function named twice by a macro body is one shipped-
        // build failure, not two. See MacroReports.
        HashSet<(TextRange Range, SymbolKey Key)>? reportedFromMacros = null;

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // FromMacro is not skipped: a dev-only function called from a macro body vanishes from
            // a release build exactly as it would called directly, and the file invoking the macro
            // is the one that stops compiling. This is the rule the flag change helps most — the
            // failure appears only once the mod ships, so an editor that stayed silent about it
            // was silent about the one class of bug this lint exists for.
            if ( !entry.IsFunctionCall )
            {
                continue;
            }

            // A call that is itself dev-only disappears alongside its target, so it is fine. The
            // range is the INVOCATION for an expanded call, which is the right question to ask:
            // what decides whether the call survives is where the macro was invoked, not where
            // its body was written.
            if ( IsInsideDevRegion(entry.Range, devRegions) )
            {
                continue;
            }

            if ( !MacroReports.ShouldReport(entry, (entry.Range, entry.Key), ref reportedFromMacros) )
            {
                continue;
            }

            // ROUTED, not looked up. SymbolExtractor keys an unqualified call written inside a class
            // body to that class, and LookupFunctions cannot answer for one: it scans a record's
            // top-level functions, where no method ever lands, and reads a null namespace as "any
            // namespace". So `error( ... )` inside cSceneObject — the inherited
            // cScriptBundleObjectBase method, which returns a bool and is not dev-only — matched the
            // unrelated `util::error` declared in a dev block in mp/_util.gsc, and every one of
            // scene_shared.gsc's thirteen calls to it was reported as a shipped-build failure.
            if ( !resolutions.TryGetValue(entry.Key, out ImmutableArray<ResolvedFunction> resolved) )
            {
                resolved = MethodResolution.ResolveCall(
                    store, askingContextId, askingPath, entry.Key, entry.Kind, askingNamespaces, fileNamespace);

                resolutions[entry.Key] = resolved;
            }

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
