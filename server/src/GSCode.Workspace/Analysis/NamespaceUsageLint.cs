using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Syntax.Ast;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// A cross-file lint: a qualified call <c>ns::foo()</c> requires the file to <c>#using</c> a
/// file that declares namespace <c>ns</c> (or for <c>ns</c> to be one of the file's own
/// namespaces). It pairs with the "add #using" code action.
///
/// ERROR severity, because the script does not LINK: the engine requires the import even though v2
/// resolution finds the function anyway, so the build is broken rather than untidy. That the analyser
/// can resolve it is a fact about the analyser, not about the game.
///
/// It was a Warning first, deliberately. The rule had only just stopped misfiring — it reported 23
/// false positives on class-method calls until this learned to skip class qualifiers below — and
/// promoting a rule to Error the same day its false positives are fixed is how red squiggles end up
/// on working code. It has since held at zero across the stock corpus, which
/// <c>CorpusDiagnosticSweepTests.NoNamespaceIsReportedUnimported</c> asserts, so a regression
/// surfaces there rather than in someone's editor.
///
/// Zero false positives by construction: if any <c>#using</c> cannot be resolved to an indexed
/// record, the whole lint is suppressed — a namespace that a not-yet-known import might supply
/// is never flagged. That property is what an Error severity rests on, so weakening any of the
/// bail-outs below now costs more than it used to.
///
/// Namespace dialects only, which is the same gate <see cref="IncludeUsageLint"/> opens on from the
/// other side. Where a file merges rather than imports there is no <c>#using</c> to add, and the
/// rule is not just inapplicable but unsatisfiable — see the comment on the check itself.
/// </summary>
public static class NamespaceUsageLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        string askingContextId = "raw",
        GameProfile? profile = null,
        FileImports? imports = null)
    {
        GameProfile game = profile ?? GameProfile.Active;

        // Only where an import is what makes a namespace reachable — the mirror of the gate
        // IncludeUsageLint opens on. On a merge dialect the rule is not merely inapplicable but
        // UNSATISFIABLE: `#using` does not lex there (Keywords.IsDirectiveEnabled), so Usings is
        // always empty, `#namespace` is off too, and the available set can never hold more than the
        // file's own stem. A CoD4 file writing `myutils::func()` alongside `#include myutils;` links
        // and runs — extraction keys the function under no namespace, so resolution finds it — and
        // the message would ask for a directive the dialect has no spelling for.
        if ( !game.ResolvesByNamespace )
        {
            return [];
        }

        // Namespaces callable without an import: the ones the file itself declares into.
        //
        // From the declarations rather than the namespace spans. The spans cover the file
        // positionally, so a file whose imports sit above its #namespace line carries a leading span
        // named after itself — which silently entered this set and suppressed the warning for any
        // call into a namespace sharing an imported file's name.
        HashSet<string> available = new(StringComparer.Ordinal);
        foreach ( string declared in result.Extraction.DeclaredNamespaces )
        {
            available.Add(declared);
        }

        // Add every namespace contributed by a #using target. Bail out (suppress the lint) the
        // moment a using can't be resolved to an indexed record — we can't know its namespaces.
        //
        // Resolved once per file by WorkspaceLints and shared with the other import lints; falling
        // back to resolving here keeps this callable on its own, which the tests rely on.
        FileImports resolvedImports = imports ?? FileImports.Resolve(result, store, language, resolver, askingPath, game);
        if ( !resolvedImports.Complete )
        {
            return [];
        }

        foreach ( ImportedFile imported in resolvedImports.Usings )
        {
            foreach ( string declared in imported.Record.DeclaredNamespaces )
            {
                available.Add(declared);
            }
        }

        // Report any qualified call whose namespace is neither the file's own nor imported.
        // Unqualified calls are keyed under the current namespace (always own), so they never
        // trip this; sys:: builtin calls have a null namespace and are skipped.
        HashSet<string> classNames = ClassNames(store);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            string? namespaceName = entry.Key.Namespace;
            if ( namespaceName is null || available.Contains(namespaceName) )
            {
                continue;
            }

            // `self._o_scene cscene::stop()` calls a class METHOD: the qualifier names a class,
            // not a namespace, and no `#using` can import one. Extraction cannot tell them apart,
            // since both are written `name::name`, so the distinction has to be drawn here where
            // the database knows what a class is. Every one of the 23 times this lint fired on
            // the stock scripts was a class — cScene, cRailTurret, cSecurityMover.
            //
            // Checked against the class's actual METHOD SET rather than merely its name. The name
            // test was right about every stock case and wrong in principle: `cScene::no_such_thing()`
            // names a real class and no real method, and reporting nothing there gave the mistake
            // nowhere to surface. The chain walk is what makes the strict form safe — an inherited
            // method is declared by an ancestor, not by the class the call names.
            if ( classNames.Contains(namespaceName)
                && MethodResolution.FindDeclaringClass(store, askingContextId, namespaceName, entry.Key.Name) is not null )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                entry.Range, DiagnosticSeverity.Error, GscDiagnosticCode.NamespaceNotImported, namespaceName));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Every class name in the language world, not merely the visible ones.
    ///
    /// Deliberately generous, matching the rest of this lint: a name that is a class ANYWHERE is
    /// never claimed to be an unimported namespace. Narrowing it to imported classes would trade
    /// a guaranteed-correct silence for a warning that might be wrong, which is the wrong
    /// direction for a lint whose whole premise is no false positives.
    /// </summary>
    private static HashSet<string> ClassNames(LanguageStore store)
    {
        // Straight off the class graph. This used to scan every record in the store, and this lint
        // runs per file, so a workspace-wide pass paid it once per file — a store scan squared.
        return [.. store.Classes.AllClassNames()];
    }
}
