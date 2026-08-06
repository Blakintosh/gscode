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
}

/// <summary>A resolved class with its declaring record.</summary>
public sealed record ResolvedClass(ClassSymbol Class, ScriptRecord Record);

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
    public static ImmutableArray<ResolvedFunction> LookupFunctions(
        LanguageStore store,
        string askingContextId,
        string askingPath,
        string? namespaceName,
        string keyName,
        bool includePrivate = false,
        ImmutableArray<string> askingNamespaces = default)
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
        foreach ( string declaringPath in store.FilesDeclaring(keyName) )
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
            }
        }

        return ApplyShadowing(
            matches.ToImmutable(),
            static match => match.Record,
            static match => match.Function.KeyName);
    }

    /// <summary>
    /// Overlay shadowing: when a mod/workspace copy and the raw copy of the SAME
    /// script-relative file both match, the overlay wins and the raw copy drops out.
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
    private static ImmutableArray<T> ApplyShadowing<T>(
        ImmutableArray<T> matches,
        Func<T, ScriptRecord> recordOf,
        Func<T, string> keyNameOf)
    {
        if ( matches.Length < 2 )
        {
            return matches;
        }

        HashSet<string> overlayIdentities = new(StringComparer.Ordinal);
        foreach ( T match in matches )
        {
            ScriptRecord record = recordOf(match);
            if ( record.ContextId != "raw" && record.RelativePath.Length > 0 )
            {
                overlayIdentities.Add(record.RelativePath + "|" + keyNameOf(match));
            }
        }

        if ( overlayIdentities.Count == 0 )
        {
            return matches;
        }

        ImmutableArray<T>.Builder kept = ImmutableArray.CreateBuilder<T>();
        foreach ( T match in matches )
        {
            ScriptRecord record = recordOf(match);
            bool shadowedOut = record.ContextId == "raw"
                && overlayIdentities.Contains(record.RelativePath + "|" + keyNameOf(match));

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

        foreach ( ScriptRecord record in store.AllRecords )
        {
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

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            if ( !importedPaths.Contains(NormalizeScriptPath(record.RelativePath)) )
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

            string normalized = NormalizeScriptPath(path);
            if ( normalized.Length > 0 && !paths.Contains(normalized) )
            {
                paths.Add(normalized);
            }
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// The comparison key for a script path written in a directive: canonical script form, minus
    /// the extension, because <c>#using</c> and <c>#include</c> name a file without one.
    /// </summary>
    private static string NormalizeScriptPath(string path)
    {
        return PathUtil.WithoutExtension(PathUtil.NormalizeScriptPath(path));
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

        string declaring = NormalizeScriptPath(declaringRelativePath);

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
                return NormalizeScriptPath(pathCall.Path) == declaring;
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
            return NormalizeScriptPath(record.RelativePath) == declaring;
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
        return CanReach(record, NormalizeScriptPath(declaringRelativePath));
    }

    private static bool CanReach(ScriptRecord record, string declaring)
    {
        if ( NormalizeScriptPath(record.RelativePath) == declaring )
        {
            return true;
        }

        foreach ( DependencyEdge edge in record.Dependencies )
        {
            if ( !edge.IsInsert && NormalizeScriptPath(edge.RawPath) == declaring )
            {
                return true;
            }
        }

        foreach ( PathCallReference pathCall in record.PathCallTargets )
        {
            if ( NormalizeScriptPath(pathCall.Path) == declaring )
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
        string relative = NormalizeScriptPath(recordRelativePath);
        return relative == NormalizeScriptPath(selfRelativePath) || includedPaths.Contains(relative);
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
        Dictionary<string, FunctionSymbol> byName = new(StringComparer.Ordinal);
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        foreach ( ScriptRecord record in store.AllRecords )
        {
            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            bool sameFile = normalizedAskingPath.Length > 0
                && string.Equals(record.Path, normalizedAskingPath, StringComparison.OrdinalIgnoreCase);

            if ( !sameFile && !includedPaths.Contains(NormalizeScriptPath(record.RelativePath)) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                byName.TryAdd(function.KeyName, function);
            }
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
        Dictionary<string, ClassSymbol> byName = new(StringComparer.Ordinal);
        string normalizedAskingPath = NormalizeAskingPath(askingPath);

        // Only the handful of files that declare a class, not every record: this runs per keystroke
        // behind statement-scope completion.
        foreach ( string path in store.Classes.AllDeclaringPaths() )
        {
            if ( !store.TryGet(path, out ScriptRecord record) )
            {
                continue;
            }

            if ( !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            bool sameFile = normalizedAskingPath.Length > 0
                && string.Equals(record.Path, normalizedAskingPath, StringComparison.OrdinalIgnoreCase);

            if ( !sameFile && !importedPaths.Contains(NormalizeScriptPath(record.RelativePath)) )
            {
                continue;
            }

            foreach ( ClassSymbol classSymbol in record.Classes )
            {
                byName.TryAdd(classSymbol.KeyName, classSymbol);
            }
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
        // every chain walk, and method resolution walks a chain per call site.
        foreach ( string path in store.Classes.PathsDeclaring(keyName) )
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
            static match => match.Class.KeyName);
    }

    /// <summary>
    /// References to a key inside GSH records. A <c>.gsh</c> serves BOTH languages, so its
    /// records live in the shared GSH store rather than either LanguageStore — this is the
    /// deliberate exception to the language-guard rule, and the only way a macro defined in a
    /// header is reachable from the <c>.gsc</c>/<c>.csc</c> that inserts it.
    ///
    /// Scans linearly: the GSH store carries no reference index, and header counts are small
    /// next to script counts. Callers should only reach for this on macro keys.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindGshReferences(
        ScriptDatabase database,
        string askingContextId,
        SymbolKey key)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( ScriptRecord record in database.AllGshRecords )
        {
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
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        ScriptDatabase database,
        ImmutableArray<LanguageStore> stores,
        string askingContextId,
        SymbolKey key)
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder results =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( LanguageStore store in stores )
        {
            results.AddRange(FindReferences(store, askingContextId, key));
        }

        // A macro declared in a .gsh lives in the shared GSH store, which serves both languages,
        // so its declaration and any header-to-header uses are invisible to a store query.
        if ( key.Kind == SymbolKind.Macro )
        {
            results.AddRange(FindGshReferences(database, askingContextId, key));
        }

        return results.ToImmutable();
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
