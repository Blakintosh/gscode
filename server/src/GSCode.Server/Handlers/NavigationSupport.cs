using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Server.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace GSCode.Server.Handlers;

/// <summary>The resolved context for a navigation request against one open document.</summary>
public sealed record NavigationTarget(
    ParseResult Result,
    string Path,
    ScriptLanguage Language,
    LanguageStore Store,
    string ContextId);

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
        LanguageStore store = _database.StoreFor(document.Language);

        return new NavigationTarget(
            document.LatestResult,
            document.Path,
            document.Language,
            store,
            ScriptDatabase.ContextIdOf(context));
    }
}
