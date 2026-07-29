using GSCode.Core;
using Serilog;
using GSCode.Core.Paths;
using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Server.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace GSCode.Server.Handlers;

/// <summary>The resolved context for a navigation request against one open document.</summary>
/// <param name="Namespaces">
/// The namespaces this file declares, carried so every query can apply the namespace-privacy
/// rule without recomputing it: a private function is visible to any file in the same namespace.
/// </param>
/// <param name="Store">
/// The single store for symbol lookups (functions, classes). A <c>.gsh</c> gets the GSC store,
/// which is arbitrary but harmless: headers declare macros, not functions or classes.
/// </param>
/// <param name="Stores">
/// Every store this file may see references in — both worlds for a <c>.gsh</c>, since a header is
/// inserted into GSC and CSC alike. Use with <see cref="DatabaseQueries.FindAllReferences"/>.
/// </param>
public sealed record NavigationTarget(
    ParseResult Result,
    string Path,
    ScriptLanguage Language,
    LanguageStore Store,
    ImmutableArray<LanguageStore> Stores,
    string ContextId,
    ImmutableArray<string> Namespaces);

/// <summary>
/// Shared plumbing for the navigation handlers: turns a document URI into its live
/// analysis plus the language store and resolution context to query against.
/// </summary>
public sealed class NavigationSupport
{
    private readonly DocumentStore _documents;
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;

    public NavigationSupport(DocumentStore documents, ScriptDatabase database, ResolverHolder resolver)
    {
        _documents = documents;
        _database = database;
        _resolver = resolver;
    }

    public ScriptDatabase Database
    {
        get { return _database; }
    }

    public PathResolver Resolver
    {
        get { return _resolver.Current; }
    }

    /// <summary>Resolves an open document, or null when it is unknown or not yet analysed.</summary>
    public NavigationTarget? Resolve(DocumentUri uri)
    {
        return Resolve(uri, freshen: false);
    }

    /// <summary>
    /// Resolves an open document, re-analysing first if the text has moved on since the last run.
    ///
    /// For features whose request carries a LIVE cursor position — completion and signature help.
    /// Analysis is debounced by 250 ms, so mid-typing the cursor offset lands in text the user has
    /// already replaced: at <c>#pre</c> the stale text might still read <c>#p</c>, the '#'-context
    /// check reads the wrong characters, and completion falls back to statement scope and offers
    /// `private`. That it worked whenever the user paused is exactly the tell.
    /// </summary>
    public NavigationTarget? ResolveFresh(DocumentUri uri)
    {
        return Resolve(uri, freshen: true);
    }

    private NavigationTarget? Resolve(DocumentUri uri, bool freshen)
    {
        string path = uri.GetFileSystemPath();
        if ( !_documents.TryGet(path, out OpenDocument document) )
        {
            return null;
        }

        ParseResult? result = freshen ? _documents.AnalyzeIfStale(document) : document.LatestResult;
        if ( result is null )
        {
            return null;
        }

        ResolutionContext context = _resolver.Current.GetContext(document.Path);

        return new NavigationTarget(
            result,
            document.Path,
            document.Language,
            _database.StoreFor(document.Language),
            _database.StoresFor(document.Language),
            ScriptDatabase.ContextIdOf(context),
            DatabaseQueries.DeclaredNamespaces(result));
    }

    /// <summary>
    /// Every reference to a key visible from this document, across both language worlds when the
    /// document is a header. The single entry point, so the count a CodeLens shows and the list a
    /// peek opens are computed the same way.
    /// </summary>
    public ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        NavigationTarget target, SymbolKey key)
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> all =
            DatabaseQueries.FindAllReferences(_database, target.Stores, target.ContextId, key);

        // Narrowing happens HERE, in the one query both the CodeLens count and the peek list run,
        // so the number and the list cannot disagree. Scoping only the lens once produced a count of
        // 0 beside a list of 1,970.
        return DatabaseQueries.ScopeToIncludeGraph(all, DeclaringFile(target, key));
    }

    /// <summary>
    /// The file whose declaration this key means FROM THIS DOCUMENT, or empty when that is not one
    /// specific file.
    ///
    /// Under a merge dialect a function has no namespace, so the key alone names every same-named
    /// function in the workspace; what disambiguates it is the asking file, which can only reach
    /// declarations it owns, imports or path-calls. Resolving from the asking document therefore
    /// answers both callers correctly with one rule: a CodeLens sits on a declaration and resolves
    /// to the file it is already in, while find-references on a call resolves to the declaration
    /// that call actually reaches.
    ///
    /// Empty on ambiguity — several reachable declarations, or none — because a wide answer is
    /// recoverable and a confidently wrong narrow one is not.
    /// </summary>
    private string DeclaringFile(NavigationTarget target, SymbolKey key)
    {
        if ( key.Kind != SymbolKind.Function || GameProfile.Active.ImportStyle != ImportStyle.Include )
        {
            return "";
        }

        if ( !target.Store.TryGet(PathUtil.NormalizeAbsolute(target.Path), out ScriptRecord asking) )
        {
            Log.Debug(
                "Reference scope: {Name} — asking file {Path} is not in the store, so no narrowing",
                key.Name, target.Path);
            return "";
        }

        // A declaration in the ASKING FILE wins outright, and this is the common case: a lens sits
        // on one. Reaching another file that happens to declare the same name does not make the
        // local symbol ambiguous — a bare main() inside combat.gsc means combat.gsc's main, even
        // though combat.gsc also path-calls cover_prone and _mgturret, which each declare their own.
        // Without this, any animscript that path-calls another animscript loses all narrowing,
        // which is every one of them.
        foreach ( FunctionSymbol declared in asking.Functions )
        {
            if ( string.Equals(declared.KeyName, key.Name, StringComparison.OrdinalIgnoreCase) )
            {
                Log.Debug(
                    "Reference scope: {Name} — declared in the asking file, scoping to '{Chosen}'",
                    key.Name, asking.RelativePath);
                return asking.RelativePath;
            }
        }

        string only = "";
        int candidates = 0;
        int reachable = 0;
        foreach ( ResolvedFunction candidate in DatabaseQueries.LookupFunctions(
            target.Store, target.ContextId, target.Path, key.Namespace, key.Name, includePrivate: true) )
        {
            candidates++;
            if ( !DatabaseQueries.Reaches(asking, candidate.Record.RelativePath) )
            {
                continue;
            }

            if ( only.Length > 0 && only != candidate.Record.RelativePath )
            {
                Log.Debug(
                    "Reference scope: {Name} — reachable from BOTH '{First}' and '{Second}', so no narrowing",
                    key.Name, only, candidate.Record.RelativePath);
                return "";
            }

            only = candidate.Record.RelativePath;
            reachable++;
        }

        Log.Debug(
            "Reference scope: {Name} — {Candidates} declarations, {Reachable} reachable from {Asking}, scoping to '{Chosen}'",
            key.Name, candidates, reachable, asking.RelativePath, only);

        return only;
    }
}
