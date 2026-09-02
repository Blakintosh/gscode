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
/// PRECONDITION: the store must hold a COMPLETE index. This is the only lint that asserts a name
/// does not exist, and that claim is worthless against a half-built one — every function in every
/// not-yet-indexed file reads as missing. The caller enforces it (see <c>WorkspaceLints</c>), rather
/// than the lint carrying a flag, so the ordering is visible where the pipeline is assembled.
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
        // one, every engine call looks unresolved. So it needs a loaded library AND a profile whose
        // library is known complete — see HasCompleteBuiltinLibrary. A partial list would report its
        // own gaps as the user's mistakes, which for BO1 would be 529 of them.
        // <paramref name="judgeUnverifiedBuiltins"/> lifts only the Verified half, for the corpus
        // harvest. Without it the gate is circular: the harvest is HOW a library's completeness gets
        // measured, so a game being brought up would report nothing at exactly the point its gaps
        // need finding. A library must still be loaded, since there is nothing to compare against
        // otherwise.
        // A third requirement, and the one that fails in practice: every import must have RESOLVED.
        // An unresolved #insert takes its macros with it, and an unexpanded macro is an ordinary
        // identifier followed by an argument list — indistinguishable from a call to a function
        // nobody has. One missing shared.gsh produced forty of these against IS_TRUE, VAL and SQR,
        // every one blaming the user for a macro they did not write. An unresolved #using is the
        // same story for a merge dialect, where an included file's functions are called unqualified.
        //
        // This is the rule the path-call case already follows: one cause, one diagnostic. The missing
        // FILE is reported (2006/5009) and the names that could only have come from it are left
        // alone. The script half keeps working, since a qualified call naming a location that does
        // not exist is wrong regardless of what any header would have defined.
        bool canJudgeBuiltins = (game.HasCompleteBuiltinLibrary || judgeUnverifiedBuiltins)
            && game.DataFilePrefix is not null
            && builtins.Count > 0
            && !ImportGate.AnyMacrosLost(result, GscDiagnosticCode.UsingNotFound);

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

        // Methods declared by this file's own classes and their ancestors. Still a bare-name set,
        // and still needed even though resolution understands methods now, for the same reason
        // ownFunctions is: the store holds the last INDEXED copy, so a method being typed right now
        // resolves to nothing until a reindex catches up.
        HashSet<string> methodNames = ClassMethodNames(result, store, askingContextId, askingPath);

        // The namespace the file declares into, for the one fallback a bare in-class call needs:
        // when it names no method at all, it may still have meant a namespace function.
        string fileNamespace = ownNamespaces.Length > 0 ? ownNamespaces[0] : "";

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(diagnosticsForMissingFiles);

        Dictionary<SymbolKey, SymbolKey> canonicalCache = [];
        FunctionLookupCache lookups = new(store, askingContextId, askingPath, ownNamespaces);

        // Keyed on the symbol AND the kind, and asked at the top of the loop: the verdict below
        // depends on nothing else, so two entries agreeing on all three always reach the same answer
        // and the second can be dropped before any of the work rather than at each of the four
        // report sites. See MacroReports.
        HashSet<(TextRange Range, SymbolKey Key, ReferenceKind Kind)>? seenFromMacros = null;

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // FromMacro is not skipped. A macro body calling a function nobody declares produces a
            // call that does not link, in every file that invokes it — and the person who has to
            // act on it is the one editing the invoking file, since a .gsh is not compiled on its
            // own and its body is never parsed as code at its definition site.
            //
            // The gates this rule already carries are what make that safe. An unresolved #insert
            // suppresses the builtin half entirely (see canJudgeBuiltins), which is the case that
            // would otherwise blame the user for a macro they did not write: an unexpanded IS_TRUE
            // is an identifier followed by an argument list, indistinguishable from a call.
            // NOT ReferenceEntry.IsFunctionCall, which the five import and privacy rules share:
            // that one excludes the arrow form, and this rule is the one that wants it. An
            // unresolved [[x]]->name() is a script function nobody declares, and saying so is the
            // whole of the MethodCall arm below.
            bool isCall = entry.Kind is ReferenceKind.Call or ReferenceKind.MethodCall;
            if ( !isCall || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            if ( !MacroReports.ShouldReport(entry, (entry.Range, entry.Key, entry.Kind), ref seenFromMacros) )
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

            // A method this file declares, or inherits from a class it can see. Gated to the call
            // shapes that could actually mean one, so a name that happens to match a method
            // somewhere cannot mask a genuinely missing namespace function.
            if ( (entry.Key.OwnerClass is not null || entry.Kind == ReferenceKind.MethodCall)
                && methodNames.Contains(entry.Key.Name) )
            {
                continue;
            }

            if ( !canonicalCache.TryGetValue(entry.Key, out SymbolKey canonical) )
            {
                canonical = MethodResolution.Canonicalize(
                    store, askingContextId, entry.Key, entry.Kind, fileNamespace);

                canonicalCache[entry.Key] = canonical;
            }

            // Resolves to a method — own, inherited, or named through a class qualifier.
            if ( canonical.OwnerClass is not null )
            {
                continue;
            }

            // Resolves to a script function (private included) — nothing to report.
            ImmutableArray<ResolvedFunction> found =
                lookups.Lookup(canonical.Namespace, canonical.Name, includePrivate: true);
            if ( found.Length > 0 )
            {
                continue;
            }

            // An arrow call is almost always a class method, and no builtin can be written
            // [[x]]->name(), so reaching here — no class declares it AND no script function has the
            // name — is the one namespace-less shape that CAN be judged on a dialect with classes.
            //
            // "Almost", and the lookup above is what covers the rest: the arrow also dispatches
            // through a FIELD holding a function pointer. gameobjects_shared.gsc writes
            // `[[self.classObj]]->onBeginUse( player )`, where onBeginUse is a top-level function
            // that dom.gsc, koth.gsc and sd.gsc assign to that field with `&onBeginUse`. Because a
            // method call carries no namespace, the lookup above searches every namespace and finds
            // it — which is why those two shipping sites are not reported here.
            if ( entry.Kind == ReferenceKind.MethodCall )
            {
                diagnostics.Add(Diagnostic.Create(
                    entry.Range,
                    DiagnosticSeverity.Error,
                    GscDiagnosticCode.ScriptFunctionNotFound,
                    entry.Key.Name));
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
            if ( isPathCall || (canonical.Namespace is not null && !DeclaresNamespace(ownNamespaces, canonical.Namespace)) )
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

            // A word another game of the lineage has as a KEYWORD, written in a dialect that does
            // not. `foreach ( x in a )` against CoD4 is the case: the lexer gates keywords per
            // profile, so `foreach` stayed an identifier, the parser read identifier-then-'(' as a
            // call, and by here it is a call to a function nobody declares. All true, and useless
            // to the person who wrote it — 5014 sends them looking for a definition instead of
            // telling them the loop is not in the game they picked.
            //
            // Asked AFTER the builtin gate above, deliberately. That keeps this a better MESSAGE
            // for a case already reported rather than a new claim in new places: the games whose
            // library we do not trust stay silent, as they must, since there an unresolved name is
            // far more likely to be an engine function we lack than a misplaced keyword.
            //
            // The dialect's own keywords cannot reach here — they lex as keywords, never as calls —
            // but the check is explicit anyway, because a message naming the current game as the
            // game that introduces the word would be nonsense and this is the only place to stop it.
            GameProfile? introducedBy = GameProfile.EarliestWithKeyword(entry.Key.Name);
            if ( introducedBy is not null && !game.IsKeyword(entry.Key.Name) )
            {
                diagnostics.Add(Diagnostic.Create(
                    entry.Range,
                    DiagnosticSeverity.Error,
                    GscDiagnosticCode.KeywordNotInDialect,
                    entry.Key.Name,
                    game.DisplayName,
                    introducedBy.DisplayName));
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

            // namespaceName is explicitly null: a class name is global, and there is no ns::Class
            // form to qualify one with. Passing the asking PATH here — which this did — compares an
            // absolute file path against ClassSymbol.Namespace, matches nothing, and silently pins
            // the walk at depth 1, so no ancestor's methods were ever collected.
            foreach ( ResolvedClass parent in DatabaseQueries.LookupClasses(
                store, askingContextId, namespaceName: null, current.ParentKeyName) )
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
