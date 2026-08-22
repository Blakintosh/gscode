using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Server.Configuration;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>Which files get diagnostics published.</summary>
public enum DiagnosticsScope
{
    /// <summary>Only documents the user has open — problems appear when a file is first opened.</summary>
    Open,

    /// <summary>Every indexed file the user could edit: their mod and workspace folders, not stock.</summary>
    Workspace,

    /// <summary>Everything indexed, including the stock scripts under the tools' raw folder.</summary>
    All,
}

/// <summary>
/// Publishes diagnostics for files that are NOT open, so a syntax error in a script you have not
/// looked at still reaches the Problems panel.
///
/// Until now <see cref="ScriptRecord.Diagnostics"/> was written on every index and never read:
/// problems existed only for open documents, which meant a broken file stayed invisible until
/// someone happened to open it.
///
/// Open documents are deliberately left alone. <see cref="TextSyncHandler"/> owns those, and its
/// set is RICHER than what a record carries — it adds the cross-file lints (unused #using,
/// private access, dev-block calls) that need the whole database and a live parse result. Both
/// publishing would fight over the same URI, and the sync handler's answer is the better one.
/// </summary>
public sealed class WorkspaceDiagnosticsPublisher
{
    private readonly ScriptDatabase _database;
    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _publisher;
    private readonly ServerSettings _settings;

    private readonly object _gate = new();

    /// <summary>
    /// Every URI this publisher has pushed a non-empty set to, so it can take them back.
    /// Diagnostics are sticky in the client: without this, narrowing the scope or fixing a file
    /// would leave the old problems on screen forever.
    /// </summary>
    private readonly HashSet<DocumentUri> _published = [];

    public WorkspaceDiagnosticsPublisher(
        ScriptDatabase database,
        DocumentStore documents,
        DiagnosticsPublisher publisher,
        ServerSettings settings)
    {
        _database = database;
        _documents = documents;
        _publisher = publisher;
        _settings = settings;
    }

    /// <summary>Maps the setting; anything unrecognised keeps the default rather than going silent.</summary>
    public static DiagnosticsScope ScopeFromSetting(string value)
    {
        if ( string.Equals(value, "open", StringComparison.OrdinalIgnoreCase) )
        {
            return DiagnosticsScope.Open;
        }

        if ( string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) )
        {
            return DiagnosticsScope.All;
        }

        return DiagnosticsScope.Workspace;
    }

    /// <summary>
    /// Whether a record is in scope. "raw" is the stock scripts: read-only, and thousands of
    /// diagnostics nobody asked for, so they need opting into explicitly.
    /// </summary>
    public static bool IsInScope(DiagnosticsScope scope, string contextId)
    {
        switch ( scope )
        {
            case DiagnosticsScope.All:
                return true;
            case DiagnosticsScope.Workspace:
                return contextId != "raw";
            default:
                return false;
        }
    }

    /// <summary>
    /// Republishes the whole workspace. Called once indexing finishes and again whenever the
    /// scope setting changes.
    /// </summary>
    public void Refresh()
    {
        DiagnosticsScope scope = ScopeFromSetting(_settings.DiagnosticsScope);

        lock ( _gate )
        {
            HashSet<DocumentUri> stillPublished = [];

            foreach ( ScriptRecord record in _database.AllRecords )
            {
                if ( !IsInScope(scope, record.ContextId) || record.Diagnostics.IsEmpty )
                {
                    continue;
                }

                // The sync handler owns open documents, and publishes a richer set for them.
                if ( _documents.TryGet(record.Path, out OpenDocument _) )
                {
                    continue;
                }

                DocumentUri uri = DocumentUri.FromFileSystemPath(record.Path);
                _publisher.Publish(uri, version: null, record.Diagnostics);
                stillPublished.Add(uri);
            }

            // Anything published last time and not this time has to be taken back explicitly.
            foreach ( DocumentUri uri in _published )
            {
                if ( !stillPublished.Contains(uri) )
                {
                    _publisher.Clear(uri);
                }
            }

            _published.Clear();
            _published.UnionWith(stillPublished);

            Log.Information(
                "Workspace diagnostics: {Count} file(s) with problems (scope: {Scope})", stillPublished.Count, scope);
        }
    }

    /// <summary>
    /// Hands a file back once it closes: the sync handler clears what it published, which would
    /// otherwise leave a file with real problems looking clean just because it was opened once.
    /// </summary>
    public void OnDocumentClosed(string path)
    {
        DiagnosticsScope scope = ScopeFromSetting(_settings.DiagnosticsScope);
        if ( scope == DiagnosticsScope.Open )
        {
            return;
        }

        if ( !_database.TryGetAnyRecord(path, out ScriptRecord record)
            || !IsInScope(scope, record.ContextId)
            || record.Diagnostics.IsEmpty )
        {
            return;
        }

        DocumentUri uri = DocumentUri.FromFileSystemPath(record.Path);
        _publisher.Publish(uri, version: null, record.Diagnostics);

        lock ( _gate )
        {
            _published.Add(uri);
        }
    }

}
