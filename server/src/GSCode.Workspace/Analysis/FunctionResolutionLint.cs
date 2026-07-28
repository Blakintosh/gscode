using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Reports a call that resolves to nothing, split across two codes by WHICH domain could have
/// explained it:
///
/// * <see cref="GscDiagnosticCode.ScriptFunctionNotFound"/> — the call named a script location
///   explicitly (<c>ns::foo()</c> for a namespace the file does not itself declare, or a
///   path-qualified <c>maps\mp\_util::foo()</c>). A builtin cannot be written that way, so the
///   failure has one possible cause.
/// * <see cref="GscDiagnosticCode.BuiltinFunctionNotFound"/> — the call was unqualified, so it could
///   have meant either a script function or an engine builtin, and neither has it. Either a typo or
///   a real builtin missing from our API data.
///
/// v1 collapsed both into <c>FunctionDoesNotExist</c> because its lookup fell back from script
/// functions to the API and returned a single verdict. Keeping them apart is what makes the second
/// code useful as a DATA SOURCE: swept over a corpus it is the candidate list for builtins the API
/// is missing, ranked by how often they are called.
///
/// Suppressed wholesale unless the game ships builtin data — without it every builtin call would
/// look unresolved. Private functions count as existing (the lookup passes
/// <c>includePrivate</c>), since "exists but is private" is <c>5003</c>'s job, not this one's.
/// </summary>
public static class FunctionResolutionLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        string askingContextId,
        string askingPath,
        BuiltinApi builtins,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;

        // No builtin library for this game means no way to tell a missing builtin from a known one.
        if ( game.DataFilePrefix is null || builtins.Count == 0 )
        {
            return [];
        }

        ImmutableArray<string> ownNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        // Path-qualified calls name a FILE, so they are script-domain regardless of dialect. They
        // are keyed with a null namespace like everything else, and are told apart by their range.
        HashSet<TextRange> pathCallRanges = [];
        foreach ( PathCallReference pathCall in result.Extraction.PathCalls )
        {
            pathCallRanges.Add(pathCall.NameRange);
        }

        // Methods callable unqualified from inside this file's classes. A method call written inside
        // a class body looks exactly like a plain call — it is keyed under the file's namespace, and
        // no namespace-level function has that name — so without this every method call in a
        // class-heavy file reads as a missing builtin. (v1 had the same step, checking the current
        // class hierarchy before falling back to the API.)
        HashSet<string> methodNames = ClassMethodNames(result, store, askingContextId, askingPath);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            // Resolves to a script function (private included) — nothing to report.
            ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
                store, askingContextId, askingPath, entry.Key.Namespace, entry.Key.Name,
                includePrivate: true, askingNamespaces: ownNamespaces);
            if ( found.Length > 0 )
            {
                continue;
            }

            if ( methodNames.Contains(entry.Key.Name) )
            {
                continue;
            }

            bool isPathCall = pathCallRanges.Contains(entry.Range);

            // An explicitly script-targeted call: a path call, or a namespace this file does not
            // declare (so it was written ns::foo, not left unqualified). Neither could be a builtin.
            if ( isPathCall || (entry.Key.Namespace is not null && !DeclaresNamespace(ownNamespaces, entry.Key.Namespace)) )
            {
                diagnostics.Add(Diagnostic.Create(
                    entry.Range,
                    DiagnosticSeverity.Warning,
                    GscDiagnosticCode.ScriptFunctionNotFound,
                    entry.Key.Name));
                continue;
            }

            // Everything else could have meant a builtin, so ask the API before reporting.
            if ( builtins.Find(entry.Key.Name) is not null )
            {
                continue;
            }

            // A null namespace is ambiguous in a dialect WITH classes: sys::foo() and a class method
            // call ([[obj]]->method()) are keyed the same way, and reporting the latter would flag
            // every method call. Only the class-less dialects can safely treat it as a plain call.
            if ( entry.Key.Namespace is null && game.HasClasses )
            {
                continue;
            }

            diagnostics.Add(Diagnostic.Create(
                entry.Range,
                DiagnosticSeverity.Warning,
                GscDiagnosticCode.BuiltinFunctionNotFound,
                entry.Key.Name));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Every method name reachable unqualified from this file: the methods of the classes it
    /// declares, plus those of their ancestors. Scoped to the file's own classes rather than every
    /// class in the workspace, so a method name somewhere unrelated cannot mask a genuinely missing
    /// builtin.
    /// </summary>
    private static HashSet<string> ClassMethodNames(
        ParseResult result, LanguageStore store, string askingContextId, string askingPath)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        if ( result.Extraction.Classes.Length == 0 )
        {
            return names;
        }

        Queue<ClassSymbol> pending = new(result.Extraction.Classes);
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

        while ( pending.Count > 0 )
        {
            ClassSymbol current = pending.Dequeue();
            if ( !visited.Add(current.KeyName) )
            {
                continue;
            }

            foreach ( FunctionSymbol method in current.Methods )
            {
                names.Add(method.KeyName);
            }

            if ( current.ParentKeyName is null )
            {
                continue;
            }

            foreach ( ResolvedClass parent in DatabaseQueries.LookupClasses(
                store, askingContextId, askingPath, current.ParentKeyName) )
            {
                pending.Enqueue(parent.Class);
            }
        }

        return names;
    }

    private static bool DeclaresNamespace(ImmutableArray<string> ownNamespaces, string namespaceName)
    {
        foreach ( string own in ownNamespaces )
        {
            if ( string.Equals(own, namespaceName, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }
}
