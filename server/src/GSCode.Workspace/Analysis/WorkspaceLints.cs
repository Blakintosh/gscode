using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Instrumentation;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Parser.Extraction;
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

        ImmutableArray<Diagnostic> all =
            lints.IsEmpty ? result.AllDiagnostics : result.AllDiagnostics.AddRange(lints);

        return ApplyPragmas(result, all);
    }

    /// <summary>
    /// Drops what an in-source pragma suppresses.
    ///
    /// Applied HERE, over the combined set, rather than inside each lint: suppression is the same
    /// idea whatever produced the diagnostic, and a parse error is as suppressible as a lint. Doing
    /// it per-lint would mean thirteen implementations of one rule, and any lint that forgot would
    /// ignore a pragma for no reason the user could see.
    /// </summary>
    private static ImmutableArray<Diagnostic> ApplyPragmas(ParseResult result, ImmutableArray<Diagnostic> diagnostics)
    {
        ImmutableArray<PragmaDirective> directives = PragmaDirectives.Scan(result.Lexed.Tokens, result.Text);
        if ( directives.IsEmpty )
        {
            return diagnostics;
        }

        ImmutableArray<Diagnostic>.Builder kept = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ( Diagnostic diagnostic in diagnostics )
        {
            if ( !PragmaDirectives.IsSuppressed(directives, diagnostic.Code, diagnostic.Range.Start.Line) )
            {
                kept.Add(diagnostic);
            }
        }

        return kept.ToImmutable();
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

        // The file's imports, resolved ONCE for the four lints that each used to resolve them
        // again. Every resolve is a filesystem probe per configured root, and this runs on every
        // keystroke — on a BO3 file the same #using list was being walked three times over.
        PerfTracker.Begin("lint.FileImports.Resolve");
        FileImports imports = FileImports.Resolve(result, store, language, resolver, path);
        PerfTracker.End();

        // First: the other #using lints abandon their pass when an import will not resolve, so
        // without this a typo silences them and says nothing about why. It deliberately does NOT
        // share the resolution above: it asks whether the target exists on DISK, which is what
        // decides whether the script links, rather than whether the index has reached it yet.
        PerfTracker.Begin("lint.UsingNotFoundLint");
        lints.AddRange(UsingNotFoundLint.Analyze(result, language, resolver, path));
        PerfTracker.End();
        PerfTracker.Begin("lint.NamespaceUsageLint");
        lints.AddRange(NamespaceUsageLint.Analyze(result, store, language, resolver, path, contextId, imports: imports));
        PerfTracker.End();
        PerfTracker.Begin("lint.UnusedUsingLint");
        lints.AddRange(UnusedUsingLint.Analyze(result, store, language, resolver, path, imports));
        PerfTracker.End();
        PerfTracker.Begin("lint.UnusedIncludeLint");
        lints.AddRange(UnusedIncludeLint.Analyze(result, store, language, resolver, path, imports));
        PerfTracker.End();
        PerfTracker.Begin("lint.AmbiguousFunctionLint");
        lints.AddRange(AmbiguousFunctionLint.Analyze(result, store, language, resolver, path, imports));
        PerfTracker.End();
        PerfTracker.Begin("lint.UnusedLocalLint");
        lints.AddRange(UnusedLocalLint.Analyze(result));
        PerfTracker.End();
        PerfTracker.Begin("lint.ThreadedResultLint");
        lints.AddRange(ThreadedResultLint.Analyze(result));
        PerfTracker.End();
        PerfTracker.Begin("lint.UnassignedVariableLint");
        lints.AddRange(UnassignedVariableLint.Analyze(result));
        PerfTracker.End();
        PerfTracker.Begin("lint.DuplicateImportLint");
        lints.AddRange(DuplicateImportLint.Analyze(result));
        PerfTracker.End();
        PerfTracker.Begin("lint.UnusedBindingLint");
        lints.AddRange(UnusedBindingLint.Analyze(result));
        PerfTracker.End();
        PerfTracker.Begin("lint.ClassCycleLint");
        lints.AddRange(ClassCycleLint.Analyze(result, store, contextId));
        PerfTracker.End();
        PerfTracker.Begin("lint.ArgumentCountLint");
        lints.AddRange(ArgumentCountLint.Analyze(result, store, contextId, path, languageBuiltins));
        PerfTracker.End();
        // One typer for all three rules that read it, and — because InferValues memoises per parse
        // — one inference walk between them. Each used to run its own: two InferAssignments and an
        // InferValues over the same tree, which was 30% of BO3's lint pass and is now 20%.
        // Whichever rule runs first pays for the walk; the other two read the same ScriptTypes.
        PerfTracker.Begin("lint.FlowTyper.ctor");
        FlowTyper typer = new(languageBuiltins, objectFields);
        PerfTracker.End();

        // The nine rules whose judgement is about one node, in ONE descent of the tree rather than
        // nine. Run here because two of them read the flow typer, whose answer has to exist first;
        // everything else in the pass is order-independent now that the result is sorted.
        PerfTracker.Begin("lint.NodeLintPass");
        NodeLintPass.Run(result, languageBuiltins, typer.InferValues(result), lints);
        PerfTracker.End();

        // What those rules do that is NOT per-node, and so has no place in the shared walk: the
        // field writes the typer collected, and the declaration-level constant checks.
        PerfTracker.Begin("lint.PreferBooleanLiteralLint.FieldWrites");
        PreferBooleanLiteralLint.InspectRest(result, objectFields, typer, lints);
        PerfTracker.End();
        PerfTracker.Begin("lint.ConstDeclarationLint.Declarations");
        ConstDeclarationLint.InspectRest(result, lints);
        PerfTracker.End();
        PerfTracker.Begin("lint.PrivateAccessLint");
        lints.AddRange(PrivateAccessLint.Analyze(result, store, contextId, path, languageBuiltins));
        PerfTracker.End();
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
            PerfTracker.Begin("lint.FunctionResolutionLint");
            lints.AddRange(FunctionResolutionLint.Analyze(
                result, store, contextId, path, languageBuiltins, resolver: resolver));
            PerfTracker.End();

            // Same precondition, one step further along: this one asserts a name is not merged into
            // scope, and before indexing finishes no file's includes have contributed anything, so
            // every cross-file call would read as missing an import.
            //
            // Handed the ENGINE NAME list rather than the game's own library, which is the only
            // reason the rule exists on MW2 at all: MW2 ships no library, and all this rule asks of
            // one is whether a name could be an engine function — a question CoD4's list answers for
            // it. Everything else here keeps reading languageBuiltins, since a signature or an
            // argument count borrowed from another game would be a confident lie.
            PerfTracker.Begin("lint.IncludeUsageLint");
            lints.AddRange(IncludeUsageLint.Analyze(
                result, store, language, resolver, path, builtins.EngineNamesFor(language), contextId,
                imports: imports));
            PerfTracker.End();

        }
        PerfTracker.Begin("lint.ReadOnlyWriteLint");
        lints.AddRange(ReadOnlyWriteLint.Analyze(result, objectFields, typer));
        PerfTracker.End();
        PerfTracker.Begin("lint.DevBlockCallLint");
        lints.AddRange(DevBlockCallLint.Analyze(
            result, store, contextId, path, DatabaseQueries.DeclaredNamespaces(result), languageBuiltins));
        PerfTracker.End();

        return InReadingOrder(lints);
    }

    /// <summary>
    /// The lints sorted by position, then by code.
    ///
    /// They came out in RULE order, which made the published order an accident of the order the
    /// calls above happen to be written in — and made every corpus comparison sensitive to it. The
    /// sweep that arbitrates a diagnostic change compares output text, so restructuring which rule
    /// walks when would have shown up as a difference with no change in what was reported.
    ///
    /// Position then code, so the order is a property of the FILE rather than of this method: two
    /// rules reporting the same position sort by their code, which is stable however they are
    /// invoked. It also happens to be the order a reader wants, since a client that does not sort
    /// shows them as given.
    /// </summary>
    private static ImmutableArray<Diagnostic> InReadingOrder(ImmutableArray<Diagnostic>.Builder lints)
    {
        lints.Sort(static (left, right) =>
        {
            int line = left.Range.Start.Line.CompareTo(right.Range.Start.Line);
            if ( line != 0 )
            {
                return line;
            }

            int character = left.Range.Start.Character.CompareTo(right.Range.Start.Character);
            if ( character != 0 )
            {
                return character;
            }

            return ((int)left.Code).CompareTo((int)right.Code);
        });

        return lints.ToImmutable();
    }
}
