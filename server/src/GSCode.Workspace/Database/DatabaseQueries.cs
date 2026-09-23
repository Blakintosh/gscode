using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Parser.Extraction;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>A resolved function with the record that declares it (for locations/paths).</summary>
public sealed record ResolvedFunction(FunctionSymbol Function, ScriptRecord Record)
{
    /// <summary>
    /// The class declaring it, when this is a method. A non-positional init property so the many
    /// existing <c>new ResolvedFunction(function, record)</c> sites keep compiling unchanged.
    /// </summary>
    public ClassSymbol? OwnerClass { get; init; }

    /// <summary>
    /// The file <see cref="FunctionSymbol.NameRange"/> is actually a position IN —
    /// <see cref="Record"/>'s path, unless the function arrived through <c>#insert</c>, in which
    /// case NameRange is a true
    /// position in the header named by <see cref="FunctionSymbol.SourceFile"/> instead. A caller
    /// building a diagnostic relation or similar file+range pair from a
    /// <see cref="ResolvedFunction"/> must use THIS as the file, not <c>Record.Path</c> directly —
    /// pairing the header-true range with the including file's path points at whatever text
    /// happens to sit at that line and column over there, which has nothing to do with where the
    /// function is actually declared.
    /// </summary>
    public string DeclaringPath
    {
        get { return Function.SourceFile.Length > 0 ? Function.SourceFile : Record.Path; }
    }
}

/// <summary>
/// A function this file could call, once it imported the script declaring it: the symbol, and the
/// script-relative path the import directive would name.
/// </summary>
public readonly record struct UnimportedFunction(FunctionSymbol Function, string ImportPath);

/// <summary>A resolved class with its declaring record.</summary>
public sealed record ResolvedClass(ClassSymbol Class, ScriptRecord Record)
{
    /// <summary>
    /// The file <see cref="ClassSymbol.NameRange"/> is actually a position IN, by the rule
    /// <see cref="ResolvedFunction.DeclaringPath"/> states at length: a class declared inside an
    /// <c>#insert</c>ed header carries a header-true range, and pairing that with the including
    /// file's path points at whatever text happens to sit at that line and column over there.
    /// </summary>
    public string DeclaringPath
    {
        get { return Class.SourceFile.Length > 0 ? Class.SourceFile : Record.Path; }
    }
}

/// <summary>
/// The files an <c>#include</c> chain reaches, and whether the walk saw all of them.
/// <see cref="Complete"/> is false when a hop did not resolve or was not indexed — the difference
/// between "these are the files" and "these are the files we could see", which is the difference
/// between a rule that may assert a name is out of scope and one that must stay quiet.
/// </summary>
public readonly record struct IncludeClosure(ImmutableArray<ScriptRecord> Records, bool Complete);

/// <summary>
/// Shared query logic over one LanguageStore. Namespace lookup MERGES across every
/// visible record contributing to the namespace; overlay shadowing dedups by
/// script-relative identity with the asking context's priority. Language never appears
/// here — the store was chosen at the entry point.
/// </summary>
public static class DatabaseQueries
{
    /// <summary>
    /// Every visible function matching namespace+name, merged across contributing files.
    /// Private functions follow the namespace-privacy rule below; <paramref name="includePrivate"/>
    /// lifts it entirely, which the private-access lint uses to tell "no such function" apart
    /// from "exists but is private".
    /// </summary>
    /// <param name="limit">
    /// Stop once this many functions are found - the first <paramref name="limit"/> of the full
    /// answer, in the same order. For callers that only ask whether a name resolves (1) or resolves
    /// to exactly one declaration (2). A bare name on a merge dialect has thousands of declarations
    /// at 50,000 files - every <c>main</c> - and building all of them to answer "yes" was what kept
    /// two lints growing with the workspace (PERF.md, the scale section).
    /// </param>
    public static ImmutableArray<ResolvedFunction> LookupFunctions(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        string? namespaceName,
        string keyName,
        bool includePrivate = false,
        ImmutableArray<string> askingNamespaces = default,
        int limit = int.MaxValue)
    {
        ImmutableArray<ResolvedFunction>.Builder matches = ImmutableArray.CreateBuilder<ResolvedFunction>();

        // Record paths are normalized; normalize the asking path once so the same-file test
        // below (which decides private visibility) can't be defeated by casing or slash style.
        // An empty asking path means "no asking file", which sees no private functions at all.
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        // The files declaring this NAME, not every file. Asked of the declaration index, which keys
        // on the same lowercase-canonical FunctionSymbol.KeyName this method compares ordinally, so
        // the candidate set is exactly what the old scan of store.AllRecords produced — and every
        // filter below it is unchanged. The index narrows where to look and decides nothing.
        //
        // It is here because this method is called once per CALL SITE by four separate lints, and
        // walking thirty thousand symbols each time made those four 97% of the cross-file lint cost.
        //
        // With a namespace, the files declaring the name INTO it — the subset the namespace filter
        // below would keep anyway. See DeclarationIndex for what the bare-name list cost at scale.
        ImmutableArray<string> declaringPaths = namespaceName is null
            ? store.FilesDeclaring(keyName)
            : store.FilesDeclaring(namespaceName, keyName);

        foreach ( string declaringPath in declaringPaths )
        {
            if ( !store.TryGet(declaringPath, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            // Overlay shadowing, decided per RECORD rather than in a second pass over the finished
            // list, so the walk can stop early. It is ApplyShadowing's rule with a store: a raw
            // record is dropped when an overlay visible to the asker sits at its relative path. The
            // rule's other half - an overlay among the matches declaring the same name - adds
            // nothing, since such an overlay is visible, non-raw and at that path, which is exactly
            // what HasOverlayAt counts.
            if ( record.ContextId == "raw" && store.HasOverlayAt(record.RelativePath, askingContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.KeyName != keyName )
                {
                    continue;
                }

                if ( namespaceName is not null && function.Namespace != namespaceName )
                {
                    continue;
                }

                if ( !includePrivate && function.IsPrivate
                    && !CanSeePrivate(function, record, normalizedAskingPath, askingNamespaces) )
                {
                    continue;
                }

                matches.Add(new ResolvedFunction(function, record));
                if ( matches.Count >= limit )
                {
                    return matches.ToImmutable();
                }
            }
        }

        return matches.ToImmutable();
    }

    /// <summary>
    /// Overlay shadowing: when a mod/workspace copy exists at the SAME script-relative path as a
    /// raw record, the engine loads ONLY the overlay — replacing the raw file WHOLESALE, whatever
    /// each copy individually declares — so every raw-context match at that path drops out, not
    /// only the ones the overlay happens to also declare under the same name.
    ///
    /// A per-NAME check here used to stand in for that (the overlay had to contribute a match under
    /// the same name for the raw one to be dropped), which is right when both copies declare the
    /// name but silently wrong when the overlay's copy deletes it: <paramref name="matches"/> is
    /// already narrowed to one name by the caller, so an overlay that no longer declares it never
    /// appears here to be compared against — the raw declaration survived, resolving to code the
    /// engine never loads.
    ///
    /// Applies to functions and to classes alike, hence the selectors — the rule is one rule, and
    /// the two were previously typed out separately, which meant a change to it had to be made
    /// twice. For classes it also decides more than tidiness: without it a mod that overrides a raw
    /// script contributes a SECOND class of the same name, and every consumer that takes the first
    /// match — the parent-chain walks in <see cref="Analysis.ClassCycleLint"/> and in method
    /// resolution — picks between them arbitrarily. Which copy wins then depends on record
    /// enumeration order, so the same edit can resolve to the raw base class one moment and the
    /// overridden one the next.
    /// </summary>
    /// <param name="store">
    /// Where <see cref="LanguageStore.HasOverlayAt"/> is asked — the fix for the gap above. Optional
    /// because one caller (<see cref="FindAllReferences"/>) aggregates across GSC, CSC and the GSH
    /// store together, none of which is uniquely "the" store its matches came from; that caller
    /// already sidesteps the per-name gap by keying <paramref name="keyNameOf"/> to a constant, so
    /// nothing here loses correctness by leaving it out.
    /// </param>
    /// <param name="askingContextId">
    /// Required whenever <paramref name="store"/> is given — <see cref="LanguageStore.HasOverlayAt"/>
    /// is a visibility question, not a bare existence one: mod_a's overlay shadows raw only when
    /// mod_a itself is asking, never a sibling mod or raw asking about its own file.
    /// </param>
    public static ImmutableArray<T> ApplyShadowing<T>(
        ImmutableArray<T> matches,
        Func<T, ScriptRecord> recordOf,
        Func<T, string> keyNameOf,
        LanguageStore? store = null,
        string askingContextId = "")
    {
        if ( matches.Length == 0 )
        {
            return matches;
        }

        // A TUPLE, not a joined string. This runs over every match, and the join allocated one
        // string per match on the way in and another per match on the way out — on the
        // workspace-symbol path with an empty query that is two strings per function in the
        // workspace, to build a key nothing keeps. A value tuple compares the same two strings
        // ordinally and allocates nothing, and it cannot be fooled by a separator appearing inside
        // a path the way a join can.
        HashSet<(string RelativePath, string KeyName)> overlayIdentities = [];
        foreach ( T match in matches )
        {
            ScriptRecord record = recordOf(match);
            if ( record.ContextId != "raw" && record.RelativePath.Length > 0 )
            {
                overlayIdentities.Add((record.RelativePath, keyNameOf(match)));
            }
        }

        ImmutableArray<T>.Builder kept = ImmutableArray.CreateBuilder<T>();
        foreach ( T match in matches )
        {
            ScriptRecord record = recordOf(match);

            // Either the overlay declared this exact name too, or — whatever it declares — an
            // overlay exists at this file's path at all, which is what actually decides whether
            // the engine ever loads the raw copy.
            bool shadowedOut = record.ContextId == "raw"
                && (overlayIdentities.Contains((record.RelativePath, keyNameOf(match)))
                    || (store?.HasOverlayAt(record.RelativePath, askingContextId) ?? false));

            if ( !shadowedOut )
            {
                kept.Add(match);
            }
        }

        return kept.ToImmutable();
    }

    /// <summary>
    /// Whether a private function is visible to the asker. Privacy in GSC is scoped to the
    /// NAMESPACE, not the file: a namespace can be split across several files, and every file
    /// declaring that namespace is part of the same logical unit, so file_b declaring
    /// `#namespace shared` may call a private function declared in file_a's `shared` block.
    /// Callers that cannot supply their namespaces fall back to same-file visibility only.
    /// </summary>
    private static bool CanSeePrivate(
        FunctionSymbol function,
        ScriptRecord record,
        string normalizedAskingPath,
        ImmutableArray<string> askingNamespaces)
    {
        if ( record.Path == normalizedAskingPath )
        {
            return true;
        }

        if ( askingNamespaces.IsDefaultOrEmpty )
        {
            return false;
        }

        foreach ( string declared in askingNamespaces )
        {
            if ( string.Equals(declared, function.Namespace, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The lowercase-canonical namespaces a file declares, for the namespace-privacy rule.
    /// Taken from the live parse result so unsaved edits count immediately.
    ///
    /// Read from the declarations, not from the namespace SPANS: the spans answer a positional
    /// question and cover the whole file, so a file whose imports sit above its <c>#namespace</c>
    /// line has a leading span named after itself. Counting that as declared handed a file the
    /// private members of any namespace that happened to share its filename.
    /// </summary>
    public static ImmutableArray<string> DeclaredNamespaces(GSCode.Parser.ParseResult result)
    {
        return result.Extraction.DeclaredNamespaces;
    }

    /// <summary>
    /// Normalizes an asking path for same-file comparisons. Callers with no asking file pass
    /// an empty string, which must stay empty rather than resolving to the process directory.
    /// </summary>
    private static string NormalizeAskingPath(string askingPath)
    {
        if ( askingPath.Length == 0 )
        {
            return "";
        }

        return PathUtil.NormalizeAbsolute(askingPath);
    }

    /// <summary>Every visible function in a namespace (for completion), deduplicated by name.</summary>
    public static ImmutableArray<FunctionSymbol> FunctionsInNamespace(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        string namespaceName,
        ImmutableArray<string> askingNamespaces = default)
    {
        Dictionary<string, FunctionSymbol> byName = new(StringComparer.Ordinal);

        // Same normalization contract as LookupFunctions: the same-file test gates privacy.
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        // The files that declare INTO this namespace, rather than every file in the store. The old
        // walk read all ~30,000 BO3 symbols to keep the few dozen in one namespace, and it is asked
        // once per namespace a file can see — so a namespace-dialect script paid for the whole store
        // several times over on every keystroke.
        foreach ( string declaringPath in store.FilesDeclaringInto(namespaceName) )
        {
            if ( !store.TryGet(declaringPath, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.Namespace != namespaceName )
                {
                    continue;
                }

                if ( function.IsPrivate && !CanSeePrivate(function, record, normalizedAskingPath, askingNamespaces) )
                {
                    continue;
                }

                byName.TryAdd(function.KeyName, function);
            }
        }

        return [.. byName.Values];
    }

    /// <summary>
    /// Functions whose name begins with <paramref name="prefix"/> and which this file CANNOT call
    /// yet, paired with the script path an import would have to name.
    ///
    /// The set completion offers with the directive attached. Every other producer answers "what is
    /// in scope here"; this one deliberately answers what is not, which is why it is the only query
    /// here keyed by a typed prefix rather than by a name or a namespace.
    ///
    /// The prefix is not a convenience — it is what makes the query affordable and the list honest.
    /// Statement scope already returns a median of 1,930 entries, and every function in a 50,000-file
    /// workspace would swamp both the list and the editor's own scoring. It is matched against the
    /// DECLARATION INDEX's keys, which are the distinct lowercase names, so the cost follows the
    /// number of names that share a prefix rather than the number of files, and nothing here walks
    /// the store.
    ///
    /// Reachability is decided by script path, not by namespace: a file is reachable when this file
    /// links against it (<see cref="LinkedScriptPaths"/>) or IS it. That is deliberately the same
    /// question in both dialect families — on BO3 an imported file's namespace is callable
    /// qualified, and on a merge dialect an included file's functions are callable bare — and in
    /// both, the fix for a function that is not reachable is one directive naming one file.
    ///
    /// Private functions are left out. Privacy is per namespace, and a file that has not imported
    /// the declaring script is not in its namespace by any route that would make the call legal.
    /// </summary>
    /// <param name="limit">
    /// The most candidates to return. The caller marks its list incomplete when this truncates, so
    /// the editor re-asks as the word narrows rather than filtering a stale page client-side.
    /// </param>
    public static ImmutableArray<UnimportedFunction> UnimportedFunctions(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        GSCode.Parser.ParseResult result,
        string prefix,
        int limit,
        GameProfile? profile = null)
    {
        if ( prefix.Length == 0 || limit <= 0 )
        {
            return [];
        }

        GameProfile game = profile ?? GameProfile.Active;

        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);
        foreach ( string linked in LinkedScriptPaths(result, game) )
        {
            reachable.Add(linked);
        }

        string normalizedAskingPath = NormalizeAskingPath(askingPath);
        ImmutableArray<UnimportedFunction>.Builder found = ImmutableArray.CreateBuilder<UnimportedFunction>();

        foreach ( string name in store.VisibleDeclaredNames(prefix.ToLowerInvariant(), askingContextId) )
        {
            foreach ( string declaringPath in store.FilesDeclaring(name) )
            {
                if ( string.Equals(declaringPath, normalizedAskingPath, StringComparison.OrdinalIgnoreCase) )
                {
                    continue;
                }

                if ( !store.TryGet(declaringPath, out ScriptRecord record)
                    || !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
                {
                    continue;
                }

                string importPath = RelativePathIndex.Normalize(record.RelativePath);
                if ( importPath.Length == 0 || reachable.Contains(importPath) )
                {
                    continue;
                }

                // A raw file a mod overlay replaces is never loaded by the engine, so offering an
                // import of it would offer a file the game does not read.
                if ( record.ContextId != askingContextId && store.HasOverlayAt(importPath, askingContextId) )
                {
                    continue;
                }

                foreach ( FunctionSymbol function in record.Functions )
                {
                    if ( function.IsPrivate
                        || !string.Equals(function.KeyName, name, StringComparison.Ordinal) )
                    {
                        continue;
                    }

                    found.Add(new UnimportedFunction(function, importPath));
                    if ( found.Count >= limit )
                    {
                        return found.ToImmutable();
                    }
                }
            }
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// The namespaces a file reaches by <c>#using</c>, excluding any the asking file declares itself
    /// (those are already offered unqualified elsewhere). For completion: what a bare word may
    /// resolve to as <c>namespace::name</c> given what is actually imported, rather than every
    /// namespace in the workspace.
    ///
    /// Read from the imported files' FUNCTIONS, not from their
    /// <see cref="ScriptRecord.Namespaces"/> spans, and that distinction is the whole point.
    /// <see cref="NamespaceSpan"/> answers a POSITIONAL question — "what namespace is in effect at
    /// this point in the file" — so a file that writes its imports above its <c>#namespace</c> line
    /// necessarily has a leading span for the region before it, named after the file (the dialect's
    /// fallback). That span is real for its purpose and must stay: a file with no <c>#namespace</c>
    /// at all has only that span, and its functions genuinely do live in the file-named namespace.
    /// But it governs no declarations here, so reading the span list handed
    /// <c>scripts\shared\util_shared</c> back both <c>util</c> AND a phantom <c>util_shared</c> —
    /// one bogus namespace per imported file, every one of them offered in the completion list.
    ///
    /// Asking the functions gets both cases right for the same reason: a namespace is reachable
    /// exactly when something is declared in it. It is also the field
    /// <see cref="FunctionsInNamespace"/> matches on, so every name returned here is guaranteed to
    /// yield at least one function rather than an empty submenu.
    /// </summary>
    public static ImmutableArray<string> ImportedNamespaces(
        LanguageStore store,
        string askingContextId,
        ImmutableArray<string> importedPaths,
        ImmutableArray<string> ownNamespaces)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        // The files each import names, from the relative-path index, rather than every record's
        // path normalized and compared — see RelativePathIndex for what that walk cost at scale.
        foreach ( ScriptRecord record in RecordsAt(store, importedPaths) )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( string declared in record.DeclaredNamespaces )
            {
                if ( !ownNamespaces.Contains(declared) )
                {
                    names.Add(declared);
                }
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The asking file's own record, then the records at each of <paramref name="normalizedPaths"/>
    /// — the candidate scope behind <see cref="FunctionsInIncludeScope"/> and
    /// <see cref="AllVisibleClasses"/>, which ask the same question about two different symbols.
    ///
    /// Only the CANDIDATES are shared. Each caller keeps its own dedupe and its own
    /// <see cref="ApplyShadowing"/> pass, because their comparers differ and folding those together
    /// would change an answer rather than tidy one.
    /// </summary>
    private static List<ScriptRecord> ScopeRecords(
        LanguageStore store, string normalizedAskingPath, ImmutableArray<string> normalizedPaths)
    {
        List<ScriptRecord> records = [];
        if ( normalizedAskingPath.Length > 0 && store.TryGet(normalizedAskingPath, out ScriptRecord asking) )
        {
            records.Add(asking);
        }

        records.AddRange(RecordsAt(store, normalizedPaths));
        return records;
    }

    /// <summary>
    /// The records at each of <paramref name="normalizedPaths"/> (already in
    /// <see cref="RelativePathIndex.Normalize"/>'s form), each path asked once.
    /// </summary>
    public static List<ScriptRecord> RecordsAt(LanguageStore store, ImmutableArray<string> normalizedPaths)
    {
        List<ScriptRecord> records = [];
        if ( normalizedPaths.IsDefaultOrEmpty )
        {
            return records;
        }

        HashSet<string> asked = new(StringComparer.Ordinal);
        foreach ( string normalizedPath in normalizedPaths )
        {
            if ( !asked.Add(normalizedPath) )
            {
                continue;
            }

            foreach ( string path in store.FilesAt(normalizedPath) )
            {
                if ( store.TryGet(path, out ScriptRecord record) )
                {
                    records.Add(record);
                }
            }
        }

        return records;
    }

    /// <summary>
    /// The script-relative paths a file imports with <c>#using</c>, lowercased with backslash
    /// separators and no extension — the form <see cref="ScriptRecord.RelativePath"/> reduces to,
    /// and the form <c>#using</c> is written in.
    ///
    /// Read from the live parse result rather than the record's dependency edges, because a
    /// <c>#using</c> edge is stored with an empty ResolvedPath and so cannot be matched by path.
    /// </summary>
    public static ImmutableArray<string> ImportedScriptPaths(GSCode.Parser.ParseResult result)
    {
        return DirectivePaths(result, ImportStyle.Namespace);
    }

    /// <summary>
    /// The script-relative paths a file LINKS AGAINST, in whichever directive its dialect spells
    /// that with: <c>#using</c> where resolution is namespace-driven, <c>#include</c> where it
    /// merges. These plus the file itself are the scope a call resolves within.
    ///
    /// The one place the two dialect families meet. Every caller that asks "what can this file
    /// reach" wants this rather than one of the two directive-specific lists below — asking for the
    /// wrong one silently returns an EMPTY array (a BO3 file has no <c>#include</c> at all), and an
    /// empty scope reads to <see cref="PreferIncludeScope"/> as "nothing matched", which falls back
    /// to the unnarrowed set. So the failure of getting this wrong is not an exception, it is a
    /// feature quietly reverting to its old behaviour, which is exactly how the namespace dialect
    /// went unnarrowed for as long as it did.
    ///
    /// The two specific lists stay public: completion genuinely wants <c>#using</c> only regardless
    /// of dialect, and the <c>#include</c> lints genuinely want <c>#include</c> only.
    /// </summary>
    public static ImmutableArray<string> LinkedScriptPaths(
        GSCode.Parser.ParseResult result, GameProfile? profile = null)
    {
        return DirectivePaths(result, (profile ?? GameProfile.Active).ImportStyle);
    }

    /// <summary>
    /// One walk of the file's top-level elements collecting the paths of whichever import directive
    /// the caller named, deduplicated and in canonical script form. Shared because the two lists
    /// differ ONLY in which node type they look for, and a second copy of the loop is a second place
    /// for the normalization to drift.
    /// </summary>
    private static ImmutableArray<string> DirectivePaths(
        GSCode.Parser.ParseResult result, ImportStyle style)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();

        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in result.Tree.Root.Elements )
        {
            string? path = element switch
            {
                GSCode.Parser.Syntax.Ast.UsingNode node when style == ImportStyle.Namespace => node.Path,
                GSCode.Parser.Syntax.Ast.IncludeNode node when style == ImportStyle.Include => node.Path,
                _ => null,
            };

            if ( path is null )
            {
                continue;
            }

            string normalized = RelativePathIndex.Normalize(path);
            if ( normalized.Length > 0 && !paths.Contains(normalized) )
            {
                paths.Add(normalized);
            }
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// The script-relative paths a file merges with <c>#include</c> (the Infinity Ward import),
    /// normalized like <see cref="ImportedScriptPaths"/>. These plus the file itself are the scope a
    /// merged, unqualified call resolves within.
    /// </summary>
    public static ImmutableArray<string> IncludedScriptPaths(GSCode.Parser.ParseResult result)
    {
        return DirectivePaths(result, ImportStyle.Include);
    }

    /// <summary>
    /// Every file an <c>#include</c> chain reaches from the asking file, transitively, with whether
    /// the walk was COMPLETE — false when a hop did not resolve or was not indexed, so the set is
    /// known to be short.
    ///
    /// Transitivity is a fact about the dialect, not a policy of whichever rule asks: the compiler
    /// flattens the chain, which the corpus settles. <c>maps\_createpath.gsc</c> includes
    /// <c>maps\_utility</c> and nothing else, calls <c>flag_init</c>, and <c>flag_init</c> lives in
    /// <c>common_scripts\utility</c> — which <c>maps\_utility</c> includes on its first line. The file
    /// ships and works.
    ///
    /// It lives here rather than inside the one lint that needed it first, so the codebase has a
    /// single place that answers the transitivity question. The direct-only helpers below
    /// (<see cref="IncludedScriptPaths"/> and its consumers) are deliberately left as they are: for
    /// completion and definition, offering or preferring too LITTLE is harmless, and widening them is
    /// a behaviour change that deserves its own measurement rather than riding along with this one.
    ///
    /// The direct includes come from the parse in hand so a directive typed a moment ago counts;
    /// every hop after that is read from the store's dependency edges. Each hop resolves against ITS
    /// OWN file's context — a mod's copy of a script includes what the mod can see, and probing those
    /// paths from the raw root would reach a different file or none at all — and resolutions are
    /// memoized, since a diamond in the graph (everything reaches <c>common_scripts\utility</c>)
    /// otherwise costs one filesystem probe per parent rather than per file.
    /// </summary>
    /// <param name="directIncludes">
    /// The file's own <c>#include</c> targets when the caller has already resolved them, which the
    /// lint pass has: resolving the same list twice in one pass is a filesystem probe per root for
    /// no new information. Default (uninitialized) means resolve them here, which keeps this usable
    /// on its own.
    /// </param>
    public static IncludeClosure IncludeClosure(
        LanguageStore store,
        Resolution.PathResolver resolver,
        GSCode.Parser.ParseResult result,
        string askingPath,
        string extension,
        ImmutableArray<ScriptRecord> directIncludes = default)
    {
        Dictionary<(Resolution.ResolutionContext Context, string Path), string?> resolved = [];
        Queue<string> pending = new();

        if ( directIncludes.IsDefault )
        {
            Resolution.ResolutionContext askingContext = resolver.GetContext(askingPath);

            foreach ( GSCode.Parser.Syntax.Ast.AstNode element in result.Tree.Root.Elements )
            {
                if ( element is not GSCode.Parser.Syntax.Ast.IncludeNode includeNode )
                {
                    continue;
                }

                if ( Probe(resolver, resolved, askingContext, includeNode.Path, extension) is not string hit )
                {
                    return new IncludeClosure([], Complete: false);
                }

                pending.Enqueue(hit);
            }
        }
        else
        {
            foreach ( ScriptRecord record in directIncludes )
            {
                pending.Enqueue(record.Path);
            }
        }

        ImmutableArray<ScriptRecord>.Builder reached = ImmutableArray.CreateBuilder<ScriptRecord>();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

        while ( pending.Count > 0 )
        {
            string includedPath = pending.Dequeue();
            if ( !visited.Add(includedPath) )
            {
                continue;
            }

            if ( !store.TryGet(includedPath, out ScriptRecord record) )
            {
                return new IncludeClosure([], Complete: false);
            }

            reached.Add(record);
            Resolution.ResolutionContext hop = resolver.GetContext(record.Path);

            foreach ( DependencyEdge edge in record.Dependencies )
            {
                if ( edge.IsInsert )
                {
                    continue;
                }

                if ( Probe(resolver, resolved, hop, edge.RawPath, extension) is not string hit )
                {
                    return new IncludeClosure([], Complete: false);
                }

                pending.Enqueue(hit);
            }
        }

        return new IncludeClosure(reached.ToImmutable(), Complete: true);
    }

    private static string? Probe(
        Resolution.PathResolver resolver,
        Dictionary<(Resolution.ResolutionContext, string), string?> memo,
        Resolution.ResolutionContext context,
        string rawPath,
        string extension)
    {
        if ( memo.TryGetValue((context, rawPath), out string? cached) )
        {
            return cached;
        }

        string? hit = resolver.Resolve(context, rawPath + extension);
        string? normalized = hit is null ? null : PathUtil.NormalizeAbsolute(hit);

        memo[(context, rawPath)] = normalized;
        return normalized;
    }

    /// <summary>
    /// Narrows references to the files that can actually REACH the declaring file, for the merge
    /// dialects.
    ///
    /// Under <c>#include</c> a function carries no namespace, so every same-named function in the
    /// workspace shares one key — 1,230 <c>main()</c>s in CoD4's animscripts. Unnarrowed, the count
    /// and the peek report all of them for any one of them: not a large answer but a wrong one.
    ///
    /// A file reaches another's function three ways, and all three must count:
    ///   1. it IS the declaring file;
    ///   2. it <c>#include</c>s it, so the function merged into its scope and is called bare;
    ///   3. it PATH-CALLS it — <c>animscripts\combat::main()</c> — which needs no import at all.
    ///
    /// Missing (3) is not a small error. The first attempt at this checked only imports, and
    /// combat.gsc's main() went from 1,230 references to zero, because every one of its real callers
    /// reaches it by path without importing it. Zero hides callers and reads as "this is dead",
    /// which is worse than the noise it replaced.
    ///
    /// Namespace-driven resolution needs it too, for a narrower reason. There the namespace IS part
    /// of the key, but a namespace is not part of a FILE: <c>scripts\mp\gametypes\_globallogic_utils.gsc</c>
    /// and <c>scripts\zm\gametypes\_globallogic_utils.gsc</c> both declare <c>#namespace
    /// globallogic_utils</c>, so one key still names two declarations and a count still merges two
    /// game modes' callers. What separates them is the <c>#using</c> graph, which
    /// <see cref="CanReach"/> already walks — a <c>#using</c> is a non-insert dependency edge just as
    /// an <c>#include</c> is, so the reachability rule is literally the same code.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> ScopeToIncludeGraph(
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        string declaringRelativePath,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        if ( declaringRelativePath.Length == 0 )
        {
            return references;
        }

        string declaring = RelativePathIndex.Normalize(declaringRelativePath);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder kept =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( (ScriptRecord record, ReferenceEntry entry) in references )
        {
            if ( MeansDeclaringFile(game, record, entry, declaring) )
            {
                kept.Add((record, entry));
            }
        }

        return kept.ToImmutable();
    }

    /// <summary>
    /// Exactly <c>ScopeToIncludeGraph(FindAllReferences(...), declaringRelativePath)</c> for a
    /// function key, read from the files that can reach the declaring file instead of from every
    /// file mentioning the key.
    ///
    /// Scoping keeps a reference only when its file is the declaring file or names it — through an
    /// import edge or a path call (see <see cref="MeansDeclaringFile"/>) — so no other file can
    /// contribute, and <see cref="LanguageStore.FilesAt"/> plus <see cref="LanguageStore.FilesNaming"/>
    /// are all of them. On a merge dialect that is the difference between reading the few files that
    /// include a script and reading every file with a <c>main</c> in it.
    ///
    /// Overlay shadowing is applied as <see cref="FindAllReferences"/> applies it, BEFORE scoping: a
    /// raw file's references drop out when a visible overlay at the same relative path references
    /// the key at all, whether or not that overlay reaches the declaring file.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindReferencesReaching(
        ImmutableArray<LanguageStore> stores,
        string askingContextId,
        SymbolKey key,
        string declaringRelativePath,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        string declaring = RelativePathIndex.Normalize(declaringRelativePath);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder kept =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( LanguageStore store in stores )
        {
            HashSet<string> reaching = new(StringComparer.Ordinal);
            reaching.UnionWith(store.FilesAt(declaring));
            reaching.UnionWith(store.FilesNaming(declaring));

            // A file must both reach the declaring file AND mention the key, so walk whichever of
            // the two lists is shorter and test membership in the other. A merge dialect's `main`
            // is mentioned everywhere and reached from few files; a namespace dialect's shared
            // utility is reached (#using'd) from nearly every file and one of its functions is
            // mentioned by far fewer.
            ImmutableArray<string> mentioning = store.FilesReferencing(key);
            IEnumerable<string> walk = mentioning.Length < reaching.Count
                ? mentioning.Where(reaching.Contains)
                : reaching;

            HashSet<string> visited = new(StringComparer.Ordinal);
            foreach ( string path in walk )
            {
                if ( !visited.Add(path) || !store.TryGet(path, out ScriptRecord record) )
                {
                    continue;
                }

                if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
                {
                    continue;
                }

                if ( record.ContextId == "raw" && AnOverlayReferences(stores, askingContextId, record.RelativePath, key) )
                {
                    continue;
                }

                foreach ( ReferenceEntry entry in record.References )
                {
                    if ( entry.Key == key && MeansDeclaringFile(game, record, entry, declaring) )
                    {
                        kept.Add((record, entry));
                    }
                }
            }
        }

        return kept.ToImmutable();
    }

    /// <summary>
    /// Whether a visible non-raw record at exactly this relative path references the key — the
    /// condition under which <see cref="FindAllReferences"/>' shadowing drops the raw copy's entries.
    /// </summary>
    private static bool AnOverlayReferences(
        ImmutableArray<LanguageStore> stores, string askingContextId, string relativePath, SymbolKey key)
    {
        if ( relativePath.Length == 0 )
        {
            return false;
        }

        string normalized = RelativePathIndex.Normalize(relativePath);
        foreach ( LanguageStore store in stores )
        {
            foreach ( string path in store.FilesAt(normalized) )
            {
                if ( !store.TryGet(path, out ScriptRecord overlay)
                    || overlay.ContextId == "raw"
                    || overlay.RelativePath != relativePath
                    || !ScriptDatabase.CanSee(askingContextId, overlay.ContextId) )
                {
                    continue;
                }

                foreach ( ReferenceEntry entry in overlay.References )
                {
                    if ( entry.Key == key )
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether ONE reference means the declaring file's function, decided per reference rather than
    /// per file — because a single file routinely holds references to several different functions
    /// that share the key. corner.gsc calls both <c>combat::main()</c> and
    /// <c>cover_behavior::main()</c>, and cover_prone.gsc calls <c>combat::main()</c> while also
    /// declaring a <c>main()</c> of its own. Keeping a whole file because it reaches the declaring
    /// one sweeps all three in.
    ///
    /// So each reference is attributed to the file it actually names:
    ///   * a PATH CALL names its file outright — the path at that exact site decides it;
    ///   * anything else is a bare name, which a merge dialect resolves locally first, so it belongs
    ///     to the referencing file when that file declares it, and otherwise to whatever it imports.
    /// </summary>
    private static bool MeansDeclaringFile(
        GameProfile game, ScriptRecord record, ReferenceEntry entry, string declaring)
    {
        foreach ( PathCallReference pathCall in record.PathCallTargets )
        {
            if ( pathCall.NameRange == entry.Range )
            {
                return RelativePathIndex.Normalize(pathCall.Path) == declaring;
            }
        }

        // A bare name in a file that declares it means THAT file's function, wherever else the name
        // also lives. This is the rule that keeps cover_prone's own main() out of combat's list.
        bool declaresItself = false;
        foreach ( FunctionSymbol function in record.Functions )
        {
            if ( DeclaresKey(game, function, entry.Key) )
            {
                declaresItself = true;
                break;
            }
        }

        if ( declaresItself )
        {
            return RelativePathIndex.Normalize(record.RelativePath) == declaring;
        }

        return CanReach(record, declaring);
    }

    /// <summary>
    /// Whether a declaration IS the symbol a key names. The name alone settles it on a merge
    /// dialect, where the key carries no namespace and cannot carry one. Where resolution is
    /// namespace-driven the namespace is half the identity, and matching on name alone attributes
    /// <c>globallogic_utils::spawn_player</c> to any file that happens to declare an unrelated
    /// <c>spawn_player</c> in a namespace of its own — which the stock scripts are full of.
    ///
    /// Routed through <see cref="GameProfile.KeyNamespace"/> rather than comparing the declared
    /// namespace directly, because a merge dialect still HAS a declared namespace (the file stem);
    /// it is just not part of the key. KeyNamespace returns null there, so the comparison is a
    /// no-op, and the merge dialects behave exactly as before.
    /// </summary>
    private static bool DeclaresKey(GameProfile game, FunctionSymbol function, SymbolKey key)
    {
        if ( !string.Equals(function.KeyName, key.Name, StringComparison.OrdinalIgnoreCase) )
        {
            return false;
        }

        return string.Equals(game.KeyNamespace(function.Namespace), key.Namespace, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a file can reach a declaring file's functions: it is that file, imports it, or path-
    /// calls it. The three ways a merge dialect makes another file's functions callable.
    /// </summary>
    public static bool Reaches(ScriptRecord record, string declaringRelativePath)
    {
        return CanReach(record, RelativePathIndex.Normalize(declaringRelativePath));
    }

    private static bool CanReach(ScriptRecord record, string declaring)
    {
        if ( RelativePathIndex.Normalize(record.RelativePath) == declaring )
        {
            return true;
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert && RelativePathIndex.Normalize(edge.RawPath) == declaring )
            {
                return true;
            }
        }

        foreach ( PathCallReference pathCall in record.PathCallTargets )
        {
            if ( RelativePathIndex.Normalize(pathCall.Path) == declaring )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a record is in an asking file's <c>#include</c> merge scope: the file itself, or one
    /// of its included files. Paths compare in normalized script-relative form.
    /// </summary>
    public static bool IsInIncludeScope(
        string recordRelativePath,
        string selfRelativePath,
        ImmutableArray<string> includedPaths)
    {
        string relative = RelativePathIndex.Normalize(recordRelativePath);
        return relative == RelativePathIndex.Normalize(selfRelativePath) || includedPaths.Contains(relative);
    }

    /// <summary>
    /// Narrows resolved definitions to the asking file's <c>#include</c> merge scope, so a call
    /// resolves to the function actually merged in rather than an unrelated file's same-named one.
    /// A PREFERENCE, not a filter: when nothing is in scope (a missing <c>#include</c>, say) the full
    /// set is returned, so go-to-definition still lands somewhere useful while the import is fixed —
    /// the same stance <see cref="LookupClasses"/> takes.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> PreferIncludeScope(
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions,
        string selfRelativePath,
        ImmutableArray<string> includedPaths)
    {
        if ( definitions.Length < 2 )
        {
            return definitions;
        }

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder scoped =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( (ScriptRecord Record, ReferenceEntry Entry) definition in definitions )
        {
            if ( IsInIncludeScope(definition.Record.RelativePath, selfRelativePath, includedPaths) )
            {
                scoped.Add(definition);
            }
        }

        return scoped.Count == 0 ? definitions : scoped.ToImmutable();
    }

    /// <summary>
    /// Every function reachable UNQUALIFIED under an <c>#include</c> dialect (for completion): this
    /// file's own, plus those in files it <c>#include</c>s DIRECTLY, all callable by bare name.
    /// Deduplicated by name, mirroring <see cref="AllVisibleClasses"/>, since there is no namespace
    /// to qualify with in the first place.
    ///
    /// Direct hops only, and deliberately narrower than the truth: the compiler flattens the chain
    /// (see <see cref="IncludeClosure"/>), so a name reached through an included file's own includes
    /// is legal here and goes unoffered. Completion errs toward offering too little — a name it
    /// misses is still typable — whereas widening it would offer, from a single <c>#include</c> of
    /// <c>maps\_utility</c>, everything CoD4's utility chain transitively reaches. The rule that must
    /// be exactly right about scope is the one that reports an Error, and that one asks
    /// <see cref="IncludeClosure"/>.
    /// </summary>
    public static ImmutableArray<FunctionSymbol> FunctionsInIncludeScope(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        ImmutableArray<string> includedPaths)
    {
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        ImmutableArray<(ScriptRecord Record, FunctionSymbol Function)>.Builder matches =
            ImmutableArray.CreateBuilder<(ScriptRecord, FunctionSymbol)>();

        // The asking file itself, then the files its includes name — read from the relative-path
        // index rather than found by normalizing every record's path, which is what this walked.
        List<ScriptRecord> inScope = ScopeRecords(store, normalizedAskingPath, includedPaths);

        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        foreach ( ScriptRecord record in inScope )
        {
            if ( !visited.Add(record.Path) || !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                matches.Add((record, function));
            }
        }

        // Overlay shadowing: a raw file and the mod overlay replacing it both match the same
        // #include-relative path, and without this a bare Dictionary.TryAdd kept whichever one
        // enumerated first — dead-code raw signature or live overlay one, by dictionary luck.
        Dictionary<string, FunctionSymbol> byName = new(StringComparer.Ordinal);
        foreach ( (ScriptRecord _, FunctionSymbol function) in ApplyShadowing(
            matches.ToImmutable(), static m => m.Record, static m => m.Function.KeyName, store, askingContextId) )
        {
            byName.TryAdd(function.KeyName, function);
        }

        return [.. byName.Values];
    }

    /// <summary>
    /// Every class this file may name (for completion), deduplicated by name.
    ///
    /// Classes are referenced by bare name, so unlike functions there is no namespace qualifier to
    /// narrow them — offering every class in the workspace meant typing "anim" suggested
    /// AnimationAdjustmentInfoXY from a file the caller never imported. A class is reachable only
    /// from its own file or from one that <c>#using</c>s it.
    ///
    /// Direct imports only, deliberately: across the 980 stock scripts every one of the 8
    /// cross-file class uses names the declaring file in its own <c>#using</c> list, so nothing
    /// real depends on an import chain.
    ///
    /// <see cref="LookupClasses"/> stays unfiltered on purpose. Completion should offer what you
    /// may legally write, but resolution should still find a class you typed without the import,
    /// so go-to-definition keeps working while you fix the missing <c>#using</c>.
    /// </summary>
    public static ImmutableArray<ClassSymbol> AllVisibleClasses(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        ImmutableArray<string> importedPaths)
    {
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        ImmutableArray<(ScriptRecord Record, ClassSymbol Class)>.Builder matches =
            ImmutableArray.CreateBuilder<(ScriptRecord, ClassSymbol)>();

        // The asking file and the files it imports, read by path: this runs per keystroke behind
        // statement-scope completion. It used to read every class-declaring file and keep the
        // imported ones, and in a large workspace every copy of a class-declaring file is one.
        List<ScriptRecord> candidates = ScopeRecords(store, normalizedAskingPath, importedPaths);

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ( ScriptRecord record in candidates )
        {
            if ( record.Classes.IsDefaultOrEmpty || !seen.Add(record.Path) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                matches.Add((record, classSymbol));
            }
        }

        // Overlay shadowing, as above: without it a mod's own override of a class is one arbitrary
        // pick away from offering the raw base's members instead of the ones the mod actually ships.
        Dictionary<string, ClassSymbol> byName = new(StringComparer.Ordinal);
        foreach ( (ScriptRecord _, ClassSymbol classSymbol) in ApplyShadowing(
            matches.ToImmutable(), static m => m.Record, static m => m.Class.KeyName, store, askingContextId) )
        {
            byName.TryAdd(classSymbol.KeyName, classSymbol);
        }

        return [.. byName.Values];
    }

    /// <summary>Every visible class matching the name (namespace optional).</summary>
    public static ImmutableArray<ResolvedClass> LookupClasses(
        LanguageStore store,
        string askingContextId,
        string? namespaceName,
        string keyName)
    {
        ImmutableArray<ResolvedClass>.Builder matches = ImmutableArray.CreateBuilder<ResolvedClass>();

        // Routed through the class graph rather than scanned: this runs once per parent link on
        // every chain walk, and method resolution walks a chain per call site. With a namespace,
        // the files declaring the class INTO it — the subset the namespace filter below keeps.
        ImmutableArray<string> declaringPaths = namespaceName is null
            ? store.Classes.PathsDeclaring(keyName)
            : store.Classes.PathsDeclaring(namespaceName, keyName);

        foreach ( string path in declaringPaths )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                if ( classSymbol.KeyName != keyName )
                {
                    continue;
                }

                if ( namespaceName is not null && classSymbol.Namespace != namespaceName )
                {
                    continue;
                }

                matches.Add(new ResolvedClass(classSymbol, record));
            }
        }

        return ApplyShadowing(
            matches.ToImmutable(),
            static match => match.Record,
            static match => match.Class.KeyName,
            store,
            askingContextId);
    }

    /// <summary>
    /// The GSC and CSC records whose analysis a header decides: the ones that <c>#insert</c> it,
    /// directly or through other headers. What a file watcher re-indexes when a header changes.
    ///
    /// A file can reach the header THROUGH ANOTHER HEADER. Headers live in a store of their own, so
    /// a direct query walks scripts alone and stops one hop in: with base.gsh inserted by
    /// wrapper.gsh inserted by script.gsc, changing base.gsh found nothing and script.gsc kept a
    /// record built against the old macro values for the rest of the session. The startup index
    /// closes the same set over the same graph, for the same chain.
    ///
    /// And a file can be waiting for a header that RESOLVES NOWHERE YET. Its insert edge records no
    /// resolved path, so no query keyed on one can find it — which is precisely the file a newly
    /// created header exists to serve. Matching the written path as well catches it, and catches
    /// the mod copy that starts shadowing a raw header too, where the dependent's edge names the
    /// file it used to resolve to rather than the one that now wins.
    /// </summary>
    /// <param name="headerRelativePath">
    /// The header as a directive would write it (<see cref="PathUtil.NormalizeScriptPath"/>'s form),
    /// or "" to match on resolved paths alone.
    /// </param>
    public static List<ScriptRecord> ScriptsInserting(
        ScriptDatabase database, string normalizedGshPath, string headerRelativePath)
    {
        // Candidates come from the directive index — the files inserting a header by its resolved
        // path, plus those writing its path — and each is still put to InsertsAny, since the
        // written-path key is looser than the comparison. It used to test every record's edges.
        string writtenKey = headerRelativePath.Length > 0 ? DirectiveIndex.WrittenKey(headerRelativePath) : "";

        HashSet<string> changed = new(StringComparer.Ordinal) { normalizedGshPath };
        Queue<string> pending = new();
        pending.Enqueue(normalizedGshPath);

        // Close over the header graph first: a header that inserts a changed one contributes
        // something different now, even though its own bytes did not move. A header whose only
        // link is a written path qualifies from the start; one linked by resolved path is found
        // when the header it inserts joins.
        List<string> headerCandidates = [];
        if ( writtenKey.Length > 0 )
        {
            headerCandidates.AddRange(database.GshFilesWriting(writtenKey));
        }

        while ( true )
        {
            while ( pending.Count > 0 )
            {
                headerCandidates.AddRange(database.GshFilesInserting(pending.Dequeue()));
            }

            if ( headerCandidates.Count == 0 )
            {
                break;
            }

            List<string> asking = headerCandidates;
            headerCandidates = [];
            foreach ( string path in asking )
            {
                if ( changed.Contains(path) || !database.TryGetGsh(path, out ScriptRecord header) )
                {
                    continue;
                }

                if ( InsertsAny(header, changed, headerRelativePath) )
                {
                    changed.Add(header.Path);
                    pending.Enqueue(header.Path);
                }
            }
        }

        List<ScriptRecord> inserting = [];
        foreach ( LanguageStore store in database.BothLanguageStores )
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<string> candidates = [];
            foreach ( string header in changed )
            {
                candidates.AddRange(store.FilesInserting(header));
            }

            if ( writtenKey.Length > 0 )
            {
                candidates.AddRange(store.FilesWriting(writtenKey));
            }

            foreach ( string path in candidates )
            {
                if ( !seen.Add(path) || !store.TryGet(path, out ScriptRecord record) )
                {
                    continue;
                }

                if ( InsertsAny(record, changed, headerRelativePath) )
                {
                    inserting.Add(record);
                }
            }
        }

        return inserting;
    }

    /// <summary>Whether one record inserts any header in the changed set, by resolved or written path.</summary>
    private static bool InsertsAny(ScriptRecord record, HashSet<string> changed, string headerRelativePath)
    {
        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert )
            {
                continue;
            }

            if ( changed.Contains(edge.ResolvedPath) )
            {
                return true;
            }

            if ( headerRelativePath.Length > 0
                && string.Equals(PathUtil.NormalizeScriptPath(edge.RawPath), headerRelativePath, StringComparison.Ordinal) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// References to a key inside GSH records. A <c>.gsh</c> serves BOTH languages, so its
    /// records live in the shared GSH store rather than either LanguageStore — this is the
    /// deliberate exception to the language-guard rule, and the only way a macro defined in a
    /// header is reachable from the <c>.gsc</c>/<c>.csc</c> that inserts it.
    ///
    /// Read through the header store's reference index. It used to scan every header, on the
    /// grounds that header counts are small next to script counts — true of a game, but a large
    /// workspace carries its own headers too, and the scan grew with it (PERF.md, the scale section).
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindGshReferences(
        ScriptDatabase database,
        string askingContextId,
        SymbolKey key)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( string path in database.GshFilesReferencing(key) )
        {
            if ( !database.TryGetGsh(path, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ReferenceEntry entry in record.References )
            {
                if ( entry.Key == key )
                {
                    results.Add((record, entry));
                }
            }
        }

        return results.ToImmutable();
    }

    /// <summary>
    /// Every reference to a key that the asking file can see: its own language world(s) plus the
    /// shared GSH store for macro keys, which is where a header's own definition and uses live.
    ///
    /// This is the one place that assembles the full set. Callers that assembled it themselves
    /// drifted apart — the CodeLens count queried a single store while clicking the lens went
    /// through the client's reference provider, so the number and the peek list disagreed.
    /// </summary>
    /// <param name="macroSpansLanguages">
    /// Whether a MACRO key widens to both language stores. Defaults true, which is right for a
    /// macro genuinely reached through a shared <c>.gsh</c> — declared once, <c>#insert</c>ed into
    /// <c>.gsc</c> and <c>.csc</c> alike, so a use in either world is a use of the same symbol and
    /// the asking file's own language decides nothing (a rename started in a .gsc must still reach
    /// every <c>.csc</c> use, or it expands to nothing — see <c>MacroRenameAcrossLanguagesTests</c>).
    ///
    /// Pass false when the CALLER already knows this key is a macro this file defines LOCALLY, no
    /// <c>#insert</c> involved — the server's navigation layer is the one caller that can know
    /// this, from the asking file's own <c>Preprocessed.Macros</c>. Left
    /// true unconditionally, two independent same-named macros — one per language file, e.g.
    /// <c>CF_CRACKS_ALL</c> separately <c>#define</c>d in <c>animation_shared.gsc</c> and its
    /// sibling <c>.csc</c> — were conflated into one: the CodeLens on the .gsc's own definition
    /// counted the .csc's unrelated one, and the peek list showed both. Ignored for anything but
    /// <see cref="SymbolKind.Macro"/>, where the isolation between language worlds is never in
    /// question — a same-named FUNCTION in the other world is a different function regardless.
    /// </param>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        ScriptDatabase database,
        ImmutableArray<LanguageStore> stores,
        string askingContextId,
        SymbolKey key,
        bool macroSpansLanguages = true)
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        bool wideMacro = key.Kind == SymbolKind.Macro && macroSpansLanguages;

        // A MACRO is not a symbol of one language world. It is declared in a .gsh, which is
        // inserted into .gsc and .csc alike, so a use in either world is a use of the same name and
        // the asking file's own language decides nothing. Scoped to the asking store, a rename
        // started in a .gsc rewrote the .gsc and the .gsh and left every .csc use spelled the old
        // way — expanding to nothing — which is the failure RenameHandler's own comment says it
        // exists to avoid. Everything else keeps the isolation: a same-named FUNCTION in the other
        // world is a different function, and conflating the two is what the split is for.
        ImmutableArray<LanguageStore> scope = wideMacro ? database.BothLanguageStores : stores;

        foreach ( LanguageStore store in scope )
        {
            results.AddRange(FindReferences(store, askingContextId, key));
        }

        // A macro declared in a .gsh lives in the shared GSH store, which serves both languages,
        // so its declaration and any header-to-header uses are invisible to a store query. Gated
        // the same way as the store widening above: a macro this file defines LOCALLY has nothing
        // in the GSH store under its name regardless, so this would return empty either way — the
        // explicit gate is about intent, not about a result this changes.
        if ( wideMacro )
        {
            results.AddRange(FindGshReferences(database, askingContextId, key));
        }

        // Overlay shadowing again: a mod overlay and the raw copy it shadows can both declare (and
        // reference) the SAME key at the SAME script-relative path — the engine only ever loads the
        // overlay, but nothing upstream of here knows that, so both copies' entries are collected.
        // Without this, go-to-definition/find-references on such a key shows both, one of them dead.
        return ApplyShadowing(results.ToImmutable(), static r => r.Record, static _ => "");
    }

    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindReferences(
        LanguageStore store,
        string askingContextId,
        SymbolKey key)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( string path in store.FilesReferencing(key) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( ReferenceEntry entry in record.References )
            {
                if ( entry.Key == key )
                {
                    results.Add((record, entry));
                }
            }
        }

        return results.ToImmutable();
    }
}
