using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Parser.Extraction;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>A resolved function with the record that declares it (for locations/paths).</summary>
public sealed record ResolvedFunction(FunctionSymbol Function, ScriptRecord Record);

/// <summary>A resolved class with its declaring record.</summary>
public sealed record ResolvedClass(ClassSymbol Class, ScriptRecord Record);

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

        foreach ( ScriptRecord record in store.AllRecords )
        {
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

        return ApplyShadowing(matches.ToImmutable());
    }

    /// <summary>
    /// Overlay shadowing: when a mod/workspace copy and the raw copy of the SAME
    /// script-relative file both match, the overlay wins and the raw copy drops out.
    /// </summary>
    private static ImmutableArray<ResolvedFunction> ApplyShadowing(ImmutableArray<ResolvedFunction> matches)
    {
        if ( matches.Length < 2 )
        {
            return matches;
        }

        HashSet<string> overlayIdentities = new(StringComparer.Ordinal);
        foreach ( ResolvedFunction match in matches )
        {
            if ( match.Record.ContextId != "raw" && match.Record.RelativePath.Length > 0 )
            {
                overlayIdentities.Add(match.Record.RelativePath + "|" + match.Function.KeyName);
            }
        }

        if ( overlayIdentities.Count == 0 )
        {
            return matches;
        }

        ImmutableArray<ResolvedFunction>.Builder kept = ImmutableArray.CreateBuilder<ResolvedFunction>();
        foreach ( ResolvedFunction match in matches )
        {
            bool shadowedOut = match.Record.ContextId == "raw"
                && overlayIdentities.Contains(match.Record.RelativePath + "|" + match.Function.KeyName);

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
    /// </summary>
    public static ImmutableArray<string> DeclaredNamespaces(GSCode.Parser.ParseResult result)
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
        foreach ( NamespaceSpan span in result.Extraction.Namespaces )
        {
            if ( !names.Contains(span.KeyName) )
            {
                names.Add(span.KeyName);
            }
        }

        return names.ToImmutable();
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
    /// The script-relative paths a file imports with <c>#using</c>, lowercased with backslash
    /// separators and no extension — the form <see cref="ScriptRecord.RelativePath"/> reduces to,
    /// and the form <c>#using</c> is written in.
    ///
    /// Read from the live parse result rather than the record's dependency edges, because a
    /// <c>#using</c> edge is stored with an empty ResolvedPath and so cannot be matched by path.
    /// </summary>
    public static ImmutableArray<string> ImportedScriptPaths(GSCode.Parser.ParseResult result)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();

        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not GSCode.Parser.Syntax.Ast.UsingNode usingNode )
            {
                continue;
            }

            string normalized = NormalizeScriptPath(usingNode.Path);
            if ( normalized.Length > 0 && !paths.Contains(normalized) )
            {
                paths.Add(normalized);
            }
        }

        return paths.ToImmutable();
    }

    private static string NormalizeScriptPath(string path)
    {
        string trimmed = path.Trim().Replace('/', '\\');
        return (System.IO.Path.ChangeExtension(trimmed, null) ?? trimmed).ToLowerInvariant();
    }

    /// <summary>
    /// The script-relative paths a file merges with <c>#include</c> (the Infinity Ward import),
    /// normalized like <see cref="ImportedScriptPaths"/>. These plus the file itself are the scope a
    /// merged, unqualified call resolves within.
    /// </summary>
    public static ImmutableArray<string> IncludedScriptPaths(GSCode.Parser.ParseResult result)
    {
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();

        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in result.Tree.Root.Elements )
        {
            if ( element is not GSCode.Parser.Syntax.Ast.IncludeNode includeNode )
            {
                continue;
            }

            string normalized = NormalizeScriptPath(includeNode.Path);
            if ( normalized.Length > 0 && !paths.Contains(normalized) )
            {
                paths.Add(normalized);
            }
        }

        return paths.ToImmutable();
    }

    /// <summary>
    /// Whether a record is in an asking file's <c>#include</c> merge scope: the file itself, or one
    /// of its included files. Paths compare in normalized script-relative form.
    /// </summary>
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
    /// A no-op under <c>#using</c>: there the namespace is already part of the key, so the question
    /// never arises and BO3 is untouched.
    /// </summary>
    public static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> ScopeToIncludeGraph(
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> references,
        string declaringRelativePath,
        GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.Active;
        if ( game.ImportStyle != ImportStyle.Include || declaringRelativePath.Length == 0 )
        {
            return references;
        }

        string declaring = NormalizeScriptPath(declaringRelativePath);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)>.Builder kept =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( (ScriptRecord record, ReferenceEntry entry) in references )
        {
            if ( MeansDeclaringFile(record, entry, declaring) )
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
    private static bool MeansDeclaringFile(ScriptRecord record, ReferenceEntry entry, string declaring)
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
            if ( string.Equals(function.KeyName, entry.Key.Name, StringComparison.OrdinalIgnoreCase) )
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

        foreach ( ScriptRecord record in store.AllRecords )
        {
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

        foreach ( ScriptRecord record in store.AllRecords )
        {
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

        return matches.ToImmutable();
    }

    /// <summary>All visible files (with exact ranges) referencing a key.</summary>
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
