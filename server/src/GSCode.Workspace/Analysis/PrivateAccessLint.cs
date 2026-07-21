using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a call to a function that exists but is declared <c>private</c> in a namespace the
/// calling file does not declare. Resolution already skips such functions, which means the
/// call would otherwise fail silently with no explanation; this turns that silence into the
/// actual reason.
///
/// Privacy is scoped to the NAMESPACE, not the file: a file declaring <c>#namespace shared</c>
/// may call a private function from another file's <c>shared</c> block, because they are the
/// same logical unit. Only a caller outside the namespace is reported.
///
/// Only fires when the normal lookup finds nothing AND a privacy-ignoring lookup finds a
/// private declaration elsewhere, so a name that resolves fine some other way is never
/// flagged. Builtin names are skipped outright: a same-named private script function must
/// not make a working builtin call look broken.
/// </summary>
public static class PrivateAccessLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        string askingContextId,
        string askingPath,
        BuiltinApi builtins)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableArray<string> askingNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            if ( builtins.Find(entry.Key.Name) is not null )
            {
                continue;
            }

            ImmutableArray<ResolvedFunction> visible = DatabaseQueries.LookupFunctions(
                store, askingContextId, askingPath, entry.Key.Namespace, entry.Key.Name,
                askingNamespaces: askingNamespaces);
            if ( visible.Length > 0 )
            {
                continue;
            }

            ImmutableArray<ResolvedFunction> includingPrivate = DatabaseQueries.LookupFunctions(
                store, askingContextId, askingPath, entry.Key.Namespace, entry.Key.Name, includePrivate: true);

            foreach ( ResolvedFunction candidate in includingPrivate )
            {
                if ( !candidate.Function.IsPrivate || candidate.Record.Path == askingPath )
                {
                    continue;
                }

                Diagnostic diagnostic = Diagnostic.Create(
                    entry.Range,
                    DiagnosticSeverity.Error,
                    GscDiagnosticCode.PrivateFunctionNotVisible,
                    candidate.Function.Name,
                    candidate.Function.Namespace);

                DiagnosticRelation declaredAt = new(
                    candidate.Record.Path, candidate.Function.NameRange, "Declared private here.");

                diagnostics.Add(diagnostic with { RelatedInformation = [declaredAt] });
                break;
            }
        }

        return diagnostics.ToImmutable();
    }
}
