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
/// namespaces). This is engine-required even though v2 resolution finds the function anyway,
/// so it is a Warning, not an error. It pairs with the "add #using" code action.
///
/// Zero false positives by construction: if any <c>#using</c> cannot be resolved to an indexed
/// record, the whole lint is suppressed — a namespace that a not-yet-known import might supply
/// is never flagged.
/// </summary>
public static class NamespaceUsageLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath)
    {
        // Namespaces callable without an import: the file's own #namespace blocks.
        HashSet<string> available = new(StringComparer.Ordinal);
        foreach ( NamespaceSpan span in result.Extraction.Namespaces )
        {
            available.Add(span.KeyName);
        }

        // Add every namespace contributed by a #using target. Bail out (suppress the lint) the
        // moment a using can't be resolved to an indexed record — we can't know its namespaces.
        ResolutionContext context = resolver.GetContext(askingPath);
        string extension = language == ScriptLanguage.Csc ? ".csc" : ".gsc";
        foreach ( AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not UsingNode usingNode )
            {
                continue;
            }

            string? resolved = resolver.Resolve(context, usingNode.Path + extension);
            if ( resolved is null )
            {
                return [];
            }

            string normalized = PathUtil.NormalizeAbsolute(resolved);
            if ( !store.TryGet(normalized, out ScriptRecord record) )
            {
                return [];
            }

            foreach ( NamespaceSpan span in record.Namespaces )
            {
                available.Add(span.KeyName);
            }
        }

        // Warn on any qualified call whose namespace is neither the file's own nor imported.
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
            if ( classNames.Contains(namespaceName) )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                entry.Range, DiagnosticSeverity.Warning, GscDiagnosticCode.NamespaceNotImported, namespaceName));
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
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach ( ScriptRecord record in store.AllRecords )
        {
            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                names.Add(classSymbol.KeyName);
            }
        }

        return names;
    }
}
