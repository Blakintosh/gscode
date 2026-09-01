using System.Collections.Frozen;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// The Infinity Ward counterpart to <see cref="NamespaceUsageLint"/>: an unqualified call to a
/// function that EXISTS in the workspace but that nothing merges into this file's scope.
///
/// The hole it fills is a consequence of how resolution works. <c>#include</c> keys a function as
/// <c>(null, name)</c>, and <see cref="DatabaseQueries.LookupFunctions"/> with a null namespace
/// searches every visible record without asking what the file imports — deliberately, so
/// go-to-definition and hover keep working while an import is missing. That makes
/// <see cref="FunctionResolutionLint"/> silent here: the call resolves, to a function in a file this
/// one never included. Hovering it shows the definition and the editor reports nothing, which is
/// exactly the report that prompted this rule.
///
/// ERROR severity, matching <see cref="GscDiagnosticCode.NamespaceNotImported"/> and for the same
/// reason: the engine will not link the call, so the script does not load. That the analyser can
/// find the function is a fact about the analyser, not about the game.
///
/// The include graph is followed TRANSITIVELY, and that was measured rather than assumed. Direct
/// includes only is what the completion path does (<see cref="DatabaseQueries.FunctionsInIncludeScope"/>),
/// where offering too little is harmless — but as an Error it reported 36 calls across the stock
/// scripts. <c>maps\_createpath.gsc</c> includes <c>maps\_utility</c> and nothing else, calls
/// <c>flag_init</c>, and <c>flag_init</c> lives in <c>common_scripts\utility</c> — which
/// <c>maps\_utility</c> includes on its first line. The file ships and works, so the compiler
/// flattens the chain.
///
/// Zero false positives by construction, which is what an Error rests on. Each gate below is a
/// separate way the rule could be wrong for a reason outside the user's control:
///
/// * only on an <c>#include</c> dialect — under <c>#using</c> the same mistake is
///   <c>5000</c>'s to report;
/// * only when the name is DECLARED SOMEWHERE — a name matching nothing is <c>5013</c>/<c>5014</c>'s
///   story, and reporting both would blame one call twice;
/// * only when the builtin library is trustworthy — a real engine function absent from our data
///   that happens to share a name with some script function would otherwise be reported as a
///   missing import;
/// * one unresolvable <c>#include</c> suppresses the pass, since a file we cannot read might be the
///   one that declares the name;
/// * an unresolved <c>#insert</c> suppresses it too — an unexpanded macro is an identifier followed
///   by an argument list, indistinguishable from a call;
/// * path calls are skipped: <c>maps\mp\_util::foo()</c> names its file outright and needs no
///   import at all.
/// </summary>
public static class IncludeUsageLint
{
    public static ImmutableArray<Diagnostic> Analyze(
        ParseResult result,
        LanguageStore store,
        ScriptLanguage language,
        PathResolver resolver,
        string askingPath,
        FrozenSet<string> engineNames,
        string askingContextId = "raw",
        GameProfile? profile = null,
        FileImports? imports = null)
    {
        GameProfile game = profile ?? GameProfile.Active;

        if ( game.ImportStyle != ImportStyle.Include )
        {
            return [];
        }

        // The library gate. This rule fires precisely when a called name IS declared by some script
        // function, so the way it goes wrong is a name that is BOTH an engine builtin we have no
        // data for and a script function somewhere — the user calls the builtin, we demand an
        // include for the unrelated script copy. It therefore needs a name list it can trust: the
        // game's own where that is complete, or a close sibling's where the game ships none at all
        // (GameProfile.EngineNameFallbackPrefix — MW2 reading CoD4's).
        //
        // WaW and BO1 qualify for neither, and measurement is why. With the gate lifted they report
        // 66 and 96 calls whose names are engine functions their own incomplete libraries lack
        // (lookatentity, setteam, getthreat); MW2 reports none at all once 'abs' is in CoD4's list.
        // A second incomplete list does not add up to a trustworthy one.
        //
        // The verdict is GameProfile's rather than re-derived here, so that a game being brought up
        // has one place to change — and so a corpus harvest can lift it by asking about a profile
        // that says it is trusted, instead of a mode parameter this rule would have to carry in
        // production for one test's sake.
        if ( !game.HasTrustedEngineNames || engineNames.Count == 0 )
        {
            return [];
        }

        // #using is not asked about: no include dialect has one, and an unresolvable #include is
        // already caught by the closure walk below reporting an incomplete set.
        if ( ImportGate.AnyUnresolved(result, GscDiagnosticCode.InsertNotFound) )
        {
            return [];
        }

        // Names merged into local scope: this file's own declarations, plus everything declared by
        // the files it includes. Taken from the parse in hand for the former, since the store holds
        // the last INDEXED copy and a function being typed right now is not in it yet.
        HashSet<string> inScope = new(StringComparer.OrdinalIgnoreCase);
        foreach ( FunctionSymbol function in result.Extraction.Functions )
        {
            inScope.Add(function.KeyName);
        }

        // The transitive walk itself belongs to the database, not to this rule — see
        // DatabaseQueries.IncludeClosure for why the chain flattens and what an incomplete walk
        // means. An incomplete one suppresses the whole pass either way: a file we could not read
        // might be the one that declares the name.
        //
        // Its direct hops come from the shared resolution when the pipeline has one, so the same
        // #include list is not probed twice in a single analysis.
        ImmutableArray<ScriptRecord> direct = default;
        if ( imports is not null )
        {
            if ( !imports.Complete )
            {
                return [];
            }

            direct = [.. imports.Includes.Select(static imported => imported.Record)];
        }

        IncludeClosure closure = DatabaseQueries.IncludeClosure(
            store, resolver, result, askingPath, game.ExtensionFor(language), direct);

        if ( !closure.Complete )
        {
            return [];
        }

        // A file with no includes at all is the shape most likely to be mid-edit or to be something
        // other than a script (a stub, a generated file), and it is also the shape where this rule
        // would produce the largest single burst of errors. Nothing is lost by staying quiet: a file
        // that calls into another file and imports nothing has one obvious problem, and the first
        // #include it gains turns the rule back on.
        if ( closure.Records.Length == 0 )
        {
            return [];
        }

        foreach ( ScriptRecord record in closure.Records )
        {
            foreach ( FunctionSymbol function in record.Functions )
            {
                inScope.Add(function.KeyName);
            }
        }

        // Path calls name their target file, so no import makes them legal or illegal. Told apart
        // from bare calls by range, the way FunctionResolutionLint does it — both are keyed with a
        // null namespace.
        HashSet<TextRange> pathCallRanges = [];
        foreach ( PathCallReference pathCall in result.Extraction.PathCalls )
        {
            pathCallRanges.Add(pathCall.NameRange);
        }

        Dictionary<string, ImmutableArray<ResolvedFunction>> lookups =
            new(StringComparer.OrdinalIgnoreCase);

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // Macro-expanded calls all key to the invocation range, so one body calling the same
        // function twice would stack the same Error on one word. The NAME is part of the key
        // because a body calling two uninclude-able functions is two imports to think about.
        HashSet<(TextRange Range, string Name)> reported = [];

        foreach ( ReferenceEntry entry in result.Extraction.References )
        {
            // FromMacro is not skipped, matching NamespaceUsageLint: a call is a call whoever wrote
            // the text, and the engine links the expansion. It is close to theoretical here — this
            // rule only runs on the include dialects, and none of them HAS a preprocessor
            // (GameProfile.HasMacros) — but a #define in one of those files is still expanded after
            // being reported as 2016, and the call it produces needs its include like any other.
            if ( entry.Kind != ReferenceKind.Call || entry.Key.Kind != SymbolKind.Function )
            {
                continue;
            }

            // A qualifier on an include dialect is a path call's, and those are exempt. Anything
            // else carrying a namespace here is not a shape this rule understands.
            if ( entry.Key.Namespace is not null || pathCallRanges.Contains(entry.Range) )
            {
                continue;
            }

            if ( inScope.Contains(entry.Key.Name) )
            {
                continue;
            }

            // The engine's own function wins: no import is needed for one, and the script copy of
            // the name is not what the call means.
            if ( engineNames.Contains(entry.Key.Name) )
            {
                continue;
            }

            // Cached per NAME, and sorted on the way in. A merge dialect can have hundreds of
            // declarations under one key (740 main()s across CoD4's raw scripts), so a file calling
            // such a name repeatedly would otherwise re-scan the store and re-sort the same list once
            // per call site. The negative result is cached too, which is the common case.
            if ( !lookups.TryGetValue(entry.Key.Name, out ImmutableArray<ResolvedFunction> declaring) )
            {
                declaring = DatabaseQueries.LookupFunctions(
                    store, askingContextId, askingPath, namespaceName: null, entry.Key.Name);

                // Sorted because store.AllRecords is in indexing order, and a message that names a
                // different file between two runs of the same workspace reads as a bug.
                declaring = declaring.Sort(static (left, right) => string.Compare(
                    left.Record.RelativePath, right.Record.RelativePath, StringComparison.OrdinalIgnoreCase));

                lookups[entry.Key.Name] = declaring;
            }

            // Declared nowhere: 5013/5014's verdict, not this one's. One cause, one diagnostic.
            if ( declaring.Length == 0 )
            {
                continue;
            }

            if ( !reported.Add((entry.Range, entry.Key.Name)) )
            {
                continue;
            }

            diagnostics.Add(Report(entry, declaring));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// The diagnostic for one call, naming a file that would fix it. <paramref name="declaring"/>
    /// arrives sorted from the cache above.
    ///
    /// The rest are attached as related information rather than dropped: a merge dialect genuinely
    /// has several same-named functions — 1,230 <c>main()</c>s across CoD4's animscripts — and which
    /// one was meant is a choice only the author can make.
    /// </summary>
    private static Diagnostic Report(ReferenceEntry entry, ImmutableArray<ResolvedFunction> declaring)
    {
        Diagnostic missing = Diagnostic.Create(
            entry.Range,
            DiagnosticSeverity.Error,
            GscDiagnosticCode.FunctionNotIncluded,
            entry.Key.Name,
            PathUtil.WithoutExtension(declaring[0].Record.RelativePath));

        if ( declaring.Length == 1 )
        {
            return missing;
        }

        // Capped: a name shared by hundreds of files would otherwise attach hundreds of links to a
        // single squiggle, and a list that long tells the reader nothing the first few do not.
        ImmutableArray<DiagnosticRelation> related =
        [
            .. declaring.Take(8).Select(resolved => new DiagnosticRelation(
                resolved.Record.Path, resolved.Function.NameRange, "Also declared here.")),
        ];

        return missing with { RelatedInformation = related };
    }
}
