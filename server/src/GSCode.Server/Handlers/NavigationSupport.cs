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
        string path = uri.GetFileSystemPath();
        if ( !_documents.TryGet(path, out OpenDocument document) || document.LatestResult is null )
        {
            return null;
        }

        ResolutionContext context = _resolver.Current.GetContext(document.Path);

        return new NavigationTarget(
            document.LatestResult,
            document.Path,
            document.Language,
            _database.StoreFor(document.Language),
            _database.StoresFor(document.Language),
            ScriptDatabase.ContextIdOf(context),
            DatabaseQueries.DeclaredNamespaces(document.LatestResult));
    }

    /// <summary>
    /// Every reference to a key visible from this document, across both language worlds when the
    /// document is a header. The single entry point, so the count a CodeLens shows and the list a
    /// peek opens are computed the same way.
    /// </summary>
    public ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> FindAllReferences(
        NavigationTarget target, SymbolKey key)
    {
        return DatabaseQueries.FindAllReferences(_database, target.Stores, target.ContextId, key);
    }
}
