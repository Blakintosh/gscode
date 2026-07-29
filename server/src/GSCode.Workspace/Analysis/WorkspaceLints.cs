using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Typing;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The cross-file lints, in one place.
///
/// These need the whole database rather than a single file — whether a <c>#using</c> is unused,
/// whether a private function is reachable, whether a call crosses into a dev block — so they
/// cannot live in the parser and used to be assembled inline in the server's text-sync handler.
/// Pulling them out lets anything with a parse result and a database run the exact set the editor
/// runs, which is what makes an offline sweep over the whole corpus meaningful: a lint audited
/// against a copy of the pipeline audits the copy.
/// </summary>
public static class WorkspaceLints
{
    /// <summary>
    /// The file's own diagnostics plus every cross-file lint that applies to it.
    /// </summary>
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        ScriptLanguage language,
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet builtins,
        ObjectFields objectFields)
    {
        ImmutableArray<Diagnostic> lints = LintsOnly(
            result, language, path, database, resolver, builtins, objectFields);

        return lints.IsEmpty ? result.AllDiagnostics : result.AllDiagnostics.AddRange(lints);
    }

    /// <summary>
    /// Just the lints, without the file's own parse diagnostics — for callers reporting on the
    /// lints alone.
    /// </summary>
    public static ImmutableArray<Diagnostic> LintsOnly(
        ParseResult result,
        ScriptLanguage language,
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet builtins,
        ObjectFields objectFields)
    {
        // GSH fragments have no language store of their own and no #using semantics to lint.
        if ( language != ScriptLanguage.Gsc && language != ScriptLanguage.Csc )
        {
            return [];
        }

        LanguageStore store = database.StoreFor(language);
        BuiltinApi languageBuiltins = builtins.For(language);
        string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));

        ImmutableArray<Diagnostic>.Builder lints = ImmutableArray.CreateBuilder<Diagnostic>();

        // First: the other #using lints abandon their pass when an import will not resolve, so
        // without this a typo silences them and says nothing about why.
        lints.AddRange(UsingNotFoundLint.Analyze(result, language, resolver, path));
        lints.AddRange(NamespaceUsageLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(UnusedUsingLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(UnusedIncludeLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(AmbiguousFunctionLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(UnusedLocalLint.Analyze(result));
        lints.AddRange(CaseLabelLint.Analyze(result));
        // One typer for both field rules: each of them runs the assignment inference, and the
        // walk is the expensive half.
        FlowTyper typer = new(languageBuiltins, objectFields);

        lints.AddRange(PreferBooleanLiteralLint.Analyze(result, languageBuiltins, objectFields, typer));
        lints.AddRange(PrivateAccessLint.Analyze(result, store, contextId, path, languageBuiltins));
        // Only once the workspace has been indexed. Every other lint degrades gracefully on a
        // partial index — a lookup that finds nothing simply offers nothing — but this one reports
        // a name as nonexistent, and before indexing finishes every script function in the
        // workspace looks nonexistent. Unlike a missing FILE, which the resolver answers from the
        // filesystem, a missing FUNCTION can only be answered by the index.
        //
        // Cannot double-report with the lint above either: this one looks up with includePrivate,
        // so a private function counts as EXISTING and only 5003 speaks for it.
        if ( database.HasCompletedIndex )
        {
            lints.AddRange(FunctionResolutionLint.Analyze(
                result, store, contextId, path, languageBuiltins, resolver: resolver));
        }
        lints.AddRange(ReadOnlyWriteLint.Analyze(result, objectFields, typer));
        lints.AddRange(DevBlockCallLint.Analyze(
            result, store, contextId, path, DatabaseQueries.DeclaredNamespaces(result), languageBuiltins));

        return lints.ToImmutable();
    }
}
