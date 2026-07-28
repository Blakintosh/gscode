using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

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
/// Both are ERRORS, because the engine's verdict is harsher than a style note: a call that resolves
/// to nothing fails to LINK, so the script does not load. That severity is only defensible because
/// the lint refuses to guess — it reports nothing at all unless it can see everything that could
/// have explained the call:
///
/// * the builtin half needs a library we trust — the game must ship one AND be verified — or the
///   library's own gaps would be reported as the user's mistakes; the script half needs only the
///   workspace, so it applies to every game;
/// * a function declared in the file being edited counts from the PARSE IN HAND, not the store,
///   which lags the buffer;
/// * private functions count as existing (the lookup passes <c>includePrivate</c>), since "exists
///   but is private" is <c>5003</c>'s story;
/// * class methods reachable unqualified from the file's own classes are excluded;
/// * a null-namespace call in a dialect with classes is skipped, since <c>sys::foo()</c> and a
///   method call are keyed alike there;
/// * a path call whose TARGET FILE does not exist reports the missing file once, rather than every
///   function called from it — the distribution not shipping a file is one problem, not thousands.
/// </summary>
public static class FunctionResolutionLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        string askingContextId,
        string askingPath,
        BuiltinApi builtins,
        GameProfile? profile = null,
        bool judgeUnverifiedBuiltins = false,
        PathResolver? resolver = null)
    {
        GameProfile game = profile ?? GameProfile.Active;

        // The two codes have different evidence requirements, so they are gated separately.
        //
        // A SCRIPT miss needs only the workspace, which every game has — an explicitly qualified call
        // that resolves to nothing is wrong whatever we know about the engine — so it is always
        // reported. A BUILTIN miss additionally requires a library we trust to be complete: without
        // one, every engine call looks unresolved. So it needs both a loaded library and a VERIFIED
        // profile, since an unverified game's library has never been measured against real scripts
        // and would report its own gaps as the user's mistakes.
        // <paramref name="judgeUnverifiedBuiltins"/> lifts only the Verified half, for the corpus
        // harvest. Without it the gate is circular: Verified means "measured against real scripts",
        // and the harvest is HOW a library gets measured — so a game being brought up would report
        // nothing at exactly the point its gaps need finding. A library must still be loaded, since
        // there is nothing to compare an unknown name against otherwise.
        bool canJudgeBuiltins = (game.Verified || judgeUnverifiedBuiltins)
            && game.DataFilePrefix is not null
            && builtins.Count > 0;

        List<Diagnostic> diagnosticsForMissingFiles = [];
        ImmutableArray<string> ownNamespaces = DatabaseQueries.DeclaredNamespaces(result);

        // Functions declared in THIS file, taken from the parse in hand rather than the store. The
        // store holds the last INDEXED copy, which lags the buffer being edited — so without this,
        // writing a function and calling it reports "not found" until a reindex catches up, which is
        // the worst possible moment to be wrong.
        HashSet<string> ownFunctions = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            ownFunctions.Add(function.KeyName);
        }

        // Path-qualified calls name a FILE, so they are script-domain regardless of dialect. They
        // are keyed with a null namespace like everything else, and are told apart by their range.
        Dictionary<TextRange, string> pathCallTargets = [];
        foreach ( PathCallReference pathCall in result.Extraction.PathCalls )
        {
            pathCallTargets[pathCall.NameRange] = pathCall.Path;
        }

        // Which of those target files do not exist. A distribution routinely ships scripts that call
        // into files it does not include — WaW's clientscripts\_fx, BO1's whole animscripts folder —
        // and every call into one is unresolvable for a single reason. Reporting each call would bury
        // the actual problem under thousands of identical errors (4,824 for one WaW file), so the
        // MISSING FILE is reported once and its calls are left alone: one cause, one diagnostic.
        HashSet<string> missingTargets = new(StringComparer.OrdinalIgnoreCase);
        if ( resolver is not null && pathCallTargets.Count > 0 )
        {
            ResolutionContext context = resolver.GetContext(askingPath);
            string extension = game.ExtensionFor(game.LanguageFromPath(askingPath));
            Dictionary<string, TextRange> firstSite = [];

            foreach ( KeyValuePair<TextRange, string> call in pathCallTargets )
            {
                if ( missingTargets.Contains(call.Value) || firstSite.ContainsKey(call.Value) )
                {
                    continue;
                }

                if ( resolver.Resolve(context, call.Value + extension) is null )
                {
                    missingTargets.Add(call.Value);
                    firstSite[call.Value] = call.Key;
                }
                else
                {
                    firstSite[call.Value] = call.Key;
                }
            }

            foreach ( string target in missingTargets )
            {
                diagnosticsForMissingFiles.Add(Diagnostic.Create(
                    firstSite[target], DiagnosticSeverity.Error, GscDiagnosticCode.UsingNotFound, target));
            }
        }

        // Methods callable unqualified from inside this file's classes. A method call written inside
        // a class body looks exactly like a plain call — it is keyed under the file's namespace, and
        // no namespace-level function has that name — so without this every method call in a
        // class-heavy file reads as a missing builtin. (v1 had the same step, checking the current
        // class hierarchy before falling back to the API.)
        HashSet<string> methodNames = ClassMethodNames(result, store, askingContextId, askingPath);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(diagnosticsForMissingFiles);

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            // Declared right here. Only counts for a call that could reach it unqualified — one
            // naming this file's own namespace, or none at all under a merge dialect.
            if ( ownFunctions.Contains(entry.Key.Name)
                && (entry.Key.Namespace is null || DeclaresNamespace(ownNamespaces, entry.Key.Namespace)) )
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

            bool isPathCall = pathCallTargets.TryGetValue(entry.Range, out string? target);

            // The target file itself is missing and has already been reported once; naming every
            // function inside it adds nothing the user can act on.
            if ( isPathCall && target is not null && missingTargets.Contains(target) )
            {
                continue;
            }

            // An explicitly script-targeted call: a path call, or a namespace this file does not
            // declare (so it was written ns::foo, not left unqualified). Neither could be a builtin.
            if ( isPathCall || (entry.Key.Namespace is not null && !DeclaresNamespace(ownNamespaces, entry.Key.Namespace)) )
            {
                diagnostics.Add(Diagnostic.Create(
                    entry.Range,
                    DiagnosticSeverity.Error,
                    GscDiagnosticCode.ScriptFunctionNotFound,
                    entry.Key.Name));
                continue;
            }

            // Everything else could have meant a builtin, so it is only reportable where the library
            // is trustworthy — and then only if the library does not have it.
            if ( !canJudgeBuiltins || builtins.Find(entry.Key.Name) is not null )
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
                DiagnosticSeverity.Error,
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
