using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
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
/// cannot live in the parser. Here, anything with a parse result and a database can run the exact
/// set the editor runs, which is what makes an offline sweep over the whole corpus meaningful: a lint
/// audited against a copy of the pipeline audits the copy.
/// </summary>
public static class WorkspaceLints
{
    /// <summary>
    /// The file's own diagnostics plus every cross-file lint that applies to it.
    /// </summary>
    /// <param name="shareTypes">See <see cref="LintsOnly"/>.</param>
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        ScriptLanguage language,
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet builtins,
        ObjectFields objectFields,
        CancellationToken cancellationToken = default,
        bool shareTypes = false)
    {
        ImmutableArray<Diagnostic> lints = LintsOnly(
            result, language, path, database, resolver, builtins, objectFields, cancellationToken,
            shareTypes: shareTypes);

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
    /// <param name="cancellationToken">
    /// Abandons a pass whose diagnostics nobody will publish. Checked at the boundaries between
    /// the pass's phases rather than before every rule: the rules are individually small, and a
    /// check per rule would be twenty identical lines buying nothing the four below do not. What
    /// they do buy is abandoning the pass BEFORE its expensive stretches — the import resolution
    /// and the shared node walk — which is where a superseded analysis wastes its time.
    /// </param>
    /// <param name="timings">
    /// Optional per-rule stopwatch, for a caller that wants one file's profile in an ORDINARY
    /// build — the corpus budget gate. Null on every production path, where the scopes are the
    /// instrumented build's <c>PerfTracker</c> ones and nothing else. See <see cref="LintTimings"/>
    /// for why the gate cannot read PerfTracker instead.
    /// </param>
    /// <param name="shareTypes">
    /// Reads the flow typer's answer through <see cref="FlowTyper.InferValuesShared"/>, so the
    /// inlay-hint and hover handlers reuse the walk this pass paid for. The server's own linter
    /// sets it. Off by default because a sweep that warms each file and then times it would
    /// otherwise time a cache hit, and report the most expensive step in the pass as free.
    /// </param>
    public static ImmutableArray<Diagnostic> LintsOnly(
        ParseResult result,
        ScriptLanguage language,
        string path,
        ScriptDatabase database,
        PathResolver resolver,
        BuiltinApiSet builtins,
        ObjectFields objectFields,
        CancellationToken cancellationToken = default,
        LintTimings? timings = null,
        bool shareTypes = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // GSH fragments have no language store of their own and no #using semantics to lint.
        if ( language != ScriptLanguage.Gsc && language != ScriptLanguage.Csc )
        {
            return [];
        }

        LanguageStore store = database.StoreFor(language);
        BuiltinApi languageBuiltins = builtins.For(language);
        string contextId = ScriptDatabase.ContextIdOf(resolver.GetContext(path));

        ImmutableArray<Diagnostic>.Builder lints = ImmutableArray.CreateBuilder<Diagnostic>();

        // The file's imports, resolved ONCE for the four lints that need them: every resolve is a
        // filesystem probe per configured root, and this runs on every keystroke.
        FileImports imports;
        using ( LintScope.For("lint.FileImports.Resolve", timings) )
        {
            imports = FileImports.Resolve(result, store, language, resolver, path);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // First: the other #using lints abandon their pass when an import will not resolve, so
        // without this a typo silences them and says nothing about why. It deliberately does NOT
        // share the resolution above: it asks whether the target exists on DISK, which is what
        // decides whether the script links, rather than whether the index has reached it yet.
        using ( LintScope.For("lint.UsingNotFoundLint", timings) )
        {
            lints.AddRange(UsingNotFoundLint.Analyze(result, language, resolver, path));
        }
        using ( LintScope.For("lint.NamespaceUsageLint", timings) )
        {
            lints.AddRange(NamespaceUsageLint.Analyze(result, store, language, resolver, path, contextId, imports: imports));
        }
        using ( LintScope.For("lint.UnusedUsingLint", timings) )
        {
            lints.AddRange(UnusedUsingLint.Analyze(result, store, language, resolver, path, imports));
        }
        using ( LintScope.For("lint.UnusedIncludeLint", timings) )
        {
            lints.AddRange(UnusedIncludeLint.Analyze(result, store, language, resolver, path, imports));
        }
        using ( LintScope.For("lint.AmbiguousFunctionLint", timings) )
        {
            lints.AddRange(AmbiguousFunctionLint.Analyze(result, store, language, resolver, path, imports));
        }
        using ( LintScope.For("lint.UnusedLocalLint", timings) )
        {
            lints.AddRange(UnusedLocalLint.Analyze(result));
        }
        using ( LintScope.For("lint.ThreadedResultLint", timings) )
        {
            lints.AddRange(ThreadedResultLint.Analyze(result));
        }
        using ( LintScope.For("lint.UnassignedVariableLint", timings) )
        {
            lints.AddRange(UnassignedVariableLint.Analyze(result));
        }
        using ( LintScope.For("lint.DuplicateImportLint", timings) )
        {
            lints.AddRange(DuplicateImportLint.Analyze(result));
        }
        using ( LintScope.For("lint.UnusedBindingLint", timings) )
        {
            lints.AddRange(UnusedBindingLint.Analyze(result));
        }
        using ( LintScope.For("lint.ClassCycleLint", timings) )
        {
            lints.AddRange(ClassCycleLint.Analyze(result, store, contextId));
        }
        using ( LintScope.For("lint.ArgumentCountLint", timings) )
        {
            lints.AddRange(ArgumentCountLint.Analyze(result, store, contextId, path, languageBuiltins));
        }
        cancellationToken.ThrowIfCancellationRequested();

        // One inference walk for all three rules that read it: separate walks cost 30% of BO3's
        // lint pass, shared 20%. In a scope of its own, BEFORE any rule reads it. Whichever caller
        // asks first pays for the walk, and when that was an argument inside NodeLintPass's scope
        // the report charged the whole flow typer to the nine per-node rules: 458 ms of cod4's
        // "NodeLintPass" was 252 ms of inference and 69 ms of the rules themselves.
        ScriptTypes types;
        using ( LintScope.For("lint.FlowTyper.InferValues", timings) )
        {
            types = shareTypes
                ? FlowTyper.InferValuesShared(result, languageBuiltins, objectFields)
                : new FlowTyper(languageBuiltins, objectFields).InferValues(result);
        }

        // The nine rules whose judgement is about one node, in ONE descent of the tree rather than
        // nine. Run here because two of them read the flow typer, whose answer has to exist first;
        // everything else in the pass is order-independent now that the result is sorted.
        using ( LintScope.For("lint.NodeLintPass", timings) )
        {
            NodeLintPass.Run(result, languageBuiltins, types, lints);
        }

        // What those rules do that is NOT per-node, and so has no place in the shared walk: the
        // field writes the typer collected, and the declaration-level constant checks.
        using ( LintScope.For("lint.PreferBooleanLiteralLint.FieldWrites", timings) )
        {
            PreferBooleanLiteralLint.InspectRest(result, objectFields, types, lints);
        }
        using ( LintScope.For("lint.ConstDeclarationLint.Declarations", timings) )
        {
            ConstDeclarationLint.InspectRest(result, lints);
        }
        using ( LintScope.For("lint.PrivateAccessLint", timings) )
        {
            lints.AddRange(PrivateAccessLint.Analyze(result, store, contextId, path, languageBuiltins));
        }
        // Only once the workspace has been indexed. Every other lint degrades gracefully on a
        // partial index — a lookup that finds nothing simply offers nothing — but this one reports
        // a name as nonexistent, and before indexing finishes every script function in the
        // workspace looks nonexistent. Unlike a missing FILE, which the resolver answers from the
        // filesystem, a missing FUNCTION can only be answered by the index.
        //
        // Cannot double-report with the lint above either: this one looks up with includePrivate,
        // so a private function counts as EXISTING and only 5003 speaks for it.
        cancellationToken.ThrowIfCancellationRequested();

        if ( database.HasCompletedIndex )
        {
            using ( LintScope.For("lint.FunctionResolutionLint", timings) )
            {
                lints.AddRange(FunctionResolutionLint.Analyze(
                    result, store, contextId, path, languageBuiltins, resolver: resolver));
            }

            // Same precondition, one step further along: this one asserts a name is not merged into
            // scope, and before indexing finishes no file's includes have contributed anything, so
            // every cross-file call would read as missing an import.
            //
            // Handed the ENGINE NAME list rather than the game's own library, which is the only
            // reason the rule exists on MW2 at all: MW2 ships no library, and all this rule asks of
            // one is whether a name could be an engine function — a question CoD4's list answers for
            // it. Everything else here keeps reading languageBuiltins, since a signature or an
            // argument count borrowed from another game would be a confident lie.
            using ( LintScope.For("lint.IncludeUsageLint", timings) )
            {
                lints.AddRange(IncludeUsageLint.Analyze(
                    result, store, language, resolver, path, builtins.EngineNamesFor(language), contextId,
                    imports: imports));
            }
        }
        using ( LintScope.For("lint.ReadOnlyWriteLint", timings) )
        {
            lints.AddRange(ReadOnlyWriteLint.Analyze(result, objectFields, types));
        }
        using ( LintScope.For("lint.DevBlockCallLint", timings) )
        {
            lints.AddRange(DevBlockCallLint.Analyze(
                result, store, contextId, path, result.Extraction.DeclaredNamespaces, languageBuiltins));
        }

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
