using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
using GSCode.Server.Configuration;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
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
/// <see cref="ScriptRecord.Diagnostics"/> is written on every index; without this it would never be
/// read, and a broken file would stay invisible until someone opened it.
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

    private readonly Lock _gate = new();

    /// <summary>
    /// Every PATH this publisher has pushed a non-empty set to, so it can take them back.
    /// Diagnostics are sticky in the client: without this, narrowing the scope or fixing a file
    /// would leave the old problems on screen forever.
    ///
    /// Paths rather than URIs, so a take-back resolves through the same seam the publish went
    /// through (<see cref="DiagnosticsPublisher.UriFor"/>) and cannot address a spelling the
    /// client was never told.
    ///
    /// Keyed to the diagnostics array LAST SENT for each path, not just the path, so a refresh
    /// sends only what changed — a refresh follows every re-lint of an edit's closed dependents,
    /// and resending every in-scope file would send a notification for every file with a problem. A
    /// record's diagnostics are replaced wholesale whenever they are recomputed, so a different
    /// array is the change signal — compared by reference, which errs toward resending, never
    /// toward staleness.
    /// </summary>
    private readonly Dictionary<string, ImmutableArray<Diagnostic>> _published = new(StringComparer.Ordinal);

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
    /// Brings the client up to date with the whole workspace: sends every in-scope file whose
    /// diagnostics differ from what was last sent, and takes back every file no longer reported.
    /// Called once indexing finishes, whenever the scope setting changes, and after closed
    /// dependents are re-linted.
    /// </summary>
    public void Refresh()
    {
        DiagnosticsScope scope = ScopeFromSetting(_settings.DiagnosticsScope);

        lock ( _gate )
        {
            HashSet<string> stillPublished = new(StringComparer.Ordinal);

            foreach ( ScriptRecord record in _database.AllRecords )
            {
                if ( !IsInScope(scope, record.ContextId) || record.Diagnostics.IsEmpty )
                {
                    continue;
                }

                // The sync handler owns open documents, and publishes a richer set for them.
                if ( _documents.IsOpen(record.Path) )
                {
                    continue;
                }

                stillPublished.Add(record.Path);

                // Unchanged since it was last sent: the client already shows exactly this.
                if ( _published.TryGetValue(record.Path, out ImmutableArray<Diagnostic> sent) && sent == record.Diagnostics )
                {
                    continue;
                }

                _publisher.Publish(record.Path, version: null, record.Diagnostics);
                _published[record.Path] = record.Diagnostics;
            }

            // Anything published last time and not this time has to be taken back explicitly.
            foreach ( string path in _published.Keys.ToList() )
            {
                if ( !stillPublished.Contains(path) )
                {
                    _publisher.Clear(path);
                    _published.Remove(path);
                }
            }

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

        _publisher.Publish(record.Path, version: null, record.Diagnostics);

        lock ( _gate )
        {
            _published[record.Path] = record.Diagnostics;
        }
    }

    /// <summary>
    /// Takes back what this publisher pushed for a file that has just been opened.
    ///
    /// The mirror of <see cref="OnDocumentClosed"/>, and needed for the same reason it is: the
    /// sync handler owns open documents and publishes a richer set for them, and the client does not
    /// treat a newer publish as replacing an older one unless it names the same document, so the set
    /// the index pushed would stand beside it and show every problem twice.
    /// </summary>
    public void OnDocumentOpened(string path)
    {
        string key = PathUtil.NormalizeAbsolute(path);

        lock ( _gate )
        {
            if ( !_published.Remove(key) )
            {
                return;
            }
        }

        _publisher.Clear(key);
    }

}
