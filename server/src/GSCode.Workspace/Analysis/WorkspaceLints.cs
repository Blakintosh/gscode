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

        lints.AddRange(NamespaceUsageLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(UnusedUsingLint.Analyze(result, store, language, resolver, path));
        lints.AddRange(PreferBooleanLiteralLint.Analyze(result, languageBuiltins));
        lints.AddRange(PrivateAccessLint.Analyze(result, store, contextId, path, languageBuiltins));
        lints.AddRange(ReadOnlyWriteLint.Analyze(result, objectFields, new FlowTyper(languageBuiltins, objectFields)));
        lints.AddRange(DevBlockCallLint.Analyze(
            result, store, contextId, path, DatabaseQueries.DeclaredNamespaces(result), languageBuiltins));

        return lints.ToImmutable();
    }
}
