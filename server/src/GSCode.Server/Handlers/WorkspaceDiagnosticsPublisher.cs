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
///
/// Three things keep a large workspace from flooding the client. A Hint is never sent for a closed
/// file (see <see cref="ForClosedFile"/>); a full-mode refresh before the startup sweep has finished
/// sends nothing, since every record it would send is about to be replaced; and a large refresh is
/// paced (see <see cref="Dispatch"/>). At 50,000 heavy BO3 files a cold start pushed 49,134
/// notifications and 366 MB in one go — twice over when a file was opened during the sweep — and
/// every response the editor was waiting for queued behind them for seconds.
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

    /// <summary>
    /// Sends still waiting their turn — a path and its diagnostics, or default to take a file back —
    /// in the order they were decided. Guarded by <see cref="_gate"/>, as is <see cref="_draining"/>.
    /// </summary>
    private readonly Queue<(string Path, ImmutableArray<Diagnostic> Diagnostics)> _pending = new();

    private bool _draining;

    /// <summary>Sends up to this many go out at once, as before; more are paced.</summary>
    private const int InlineSends = 256;

    /// <summary>Files sent per paced batch.</summary>
    private const int BatchSends = 100;

    /// <summary>
    /// The pause between paced batches. A batch of heavy files is a few hundred KB, which the
    /// transport writes in a few tens of milliseconds; pausing as long again leaves room for every
    /// response produced meanwhile to go out next, instead of after the whole push.
    /// </summary>
    private static readonly TimeSpan BatchPause = TimeSpan.FromMilliseconds(25);

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
    /// <summary>
    /// Closed files re-indexed and not yet linted again, by how many holders are waiting on them. A
    /// re-index stores the parse diagnostics alone; in full mode the cross-file ones come back only
    /// with the relint, so a refresh in between would take them out of the Problems panel and the
    /// next one put them back. Their last-published set stands until <see cref="Release"/>.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _awaitingRelint = new(StringComparer.Ordinal);

    /// <summary>Holds what these closed files last published until <see cref="Release"/> — see <see cref="_awaitingRelint"/>.</summary>
    public void HoldUntilRelinted(IEnumerable<string> normalizedPaths)
    {
        foreach ( string path in normalizedPaths )
        {
            _awaitingRelint.AddOrUpdate(path, 1, static (_, count) => count + 1);
        }
    }

    /// <summary>Ends a <see cref="HoldUntilRelinted"/> for these paths; the next refresh sends what they hold now.</summary>
    public void Release(IEnumerable<string> normalizedPaths)
    {
        foreach ( string path in normalizedPaths )
        {
            while ( _awaitingRelint.TryGetValue(path, out int count) )
            {
                if ( count <= 1 )
                {
                    if ( _awaitingRelint.TryRemove(new KeyValuePair<string, int>(path, count)) )
                    {
                        break;
                    }
                }
                else if ( _awaitingRelint.TryUpdate(path, count - 1, count) )
                {
                    break;
                }
            }
        }
    }

    public void Refresh()
    {
        DiagnosticsScope scope = ScopeFromSetting(_settings.DiagnosticsScope);

        // In full mode the startup sweep replaces every record's diagnostics, and refreshes again
        // when it is done. One asked for before then — a file opened during the sweep schedules its
        // dependents, which ends here — would push every record's parse-only set, all of it to be
        // sent again moments later.
        if ( string.Equals(_settings.WorkspaceIndexingMode, "full", StringComparison.OrdinalIgnoreCase)
            && !_database.HasCompletedLintSweep )
        {
            return;
        }

        lock ( _gate )
        {
            HashSet<string> stillPublished = new(StringComparer.Ordinal);
            List<(string Path, ImmutableArray<Diagnostic> Diagnostics)> sends = [];

            foreach ( ScriptRecord record in _database.AllRecords )
            {
                if ( !IsInScope(scope, record.ContextId) )
                {
                    continue;
                }

                // The sync handler owns open documents, and publishes a richer set for them.
                if ( _documents.IsOpen(record.Path) )
                {
                    continue;
                }

                // Re-indexed and not linted again yet: what the client shows is still the better set.
                // Asked before the empty test, since a parse-only set is often empty.
                if ( _awaitingRelint.ContainsKey(record.Path) )
                {
                    if ( _published.ContainsKey(record.Path) )
                    {
                        stillPublished.Add(record.Path);
                    }

                    continue;
                }

                if ( record.Diagnostics.IsEmpty )
                {
                    continue;
                }

                // Unchanged since it was last sent: the client already shows exactly this. The same
                // array is the cheap test; a recomputed one is compared by value, since a re-index
                // builds new arrays whether or not anything in them moved — a branch switch that
                // rewrote 5,000 bodies at 50,000 heavy BO3 files sent 3,561 files' unchanged sets.
                if ( _published.TryGetValue(record.Path, out ImmutableArray<Diagnostic> sent)
                    && (sent == record.Diagnostics || SameDiagnostics(sent, record.Diagnostics)) )
                {
                    _published[record.Path] = record.Diagnostics;
                    stillPublished.Add(record.Path);
                    continue;
                }

                ImmutableArray<Diagnostic> shown = ForClosedFile(record.Diagnostics);
                if ( shown.IsEmpty )
                {
                    continue;
                }

                stillPublished.Add(record.Path);
                sends.Add((record.Path, shown));
                _published[record.Path] = record.Diagnostics;
            }

            // Anything published last time and not this time has to be taken back explicitly.
            foreach ( string path in _published.Keys.ToList() )
            {
                if ( !stillPublished.Contains(path) )
                {
                    sends.Add((path, default));
                    _published.Remove(path);
                }
            }

            Dispatch(sends);

            Log.Information(
                "Workspace diagnostics: {Count} file(s) with problems (scope: {Scope})", stillPublished.Count, scope);
        }
    }

    /// <summary>
    /// Whether two sets say the same thing, in the same order. <see cref="Diagnostic"/>'s own equality
    /// compares its tag and related-information arrays by reference, so it cannot answer this.
    /// </summary>
    private static bool SameDiagnostics(ImmutableArray<Diagnostic> left, ImmutableArray<Diagnostic> right)
    {
        if ( left.Length != right.Length )
        {
            return false;
        }

        for ( int index = 0; index < left.Length; index++ )
        {
            Diagnostic a = left[index];
            Diagnostic b = right[index];
            if ( a.Range != b.Range || a.Severity != b.Severity || a.Code != b.Code
                || !string.Equals(a.Message, b.Message, StringComparison.Ordinal)
                || !a.Tags.SequenceEqual(b.Tags) || !a.RelatedInformation.SequenceEqual(b.RelatedInformation) )
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What a CLOSED file's Problems entry shows: its diagnostics without the Hints.
    ///
    /// A Hint is an in-editor mark — VS Code renders it as faded text in the open file and leaves it
    /// out of the Problems panel — so for a closed file it reaches nothing, and when the file opens
    /// the sync handler sends its own full set. Most of what the lints produce is Hints: at 10,000
    /// heavy BO3 files 11,835 of 13,857 sampled diagnostics, unused bindings and locals above all.
    /// </summary>
    private static ImmutableArray<Diagnostic> ForClosedFile(ImmutableArray<Diagnostic> diagnostics)
    {
        bool anyHint = false;
        foreach ( Diagnostic diagnostic in diagnostics )
        {
            if ( diagnostic.Severity == DiagnosticSeverity.Hint )
            {
                anyHint = true;
                break;
            }
        }

        if ( !anyHint )
        {
            return diagnostics;
        }

        return [.. diagnostics.Where(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hint)];
    }

    /// <summary>
    /// Sends what a refresh decided, in order. A few go out at once, as they always did; a large
    /// set, or anything decided while one is still going out, joins the queue a background loop
    /// sends in paced batches. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void Dispatch(List<(string Path, ImmutableArray<Diagnostic> Diagnostics)> sends)
    {
        if ( !_draining && sends.Count <= InlineSends )
        {
            foreach ( (string Path, ImmutableArray<Diagnostic> Diagnostics) send in sends )
            {
                Send(send);
            }

            return;
        }

        foreach ( (string Path, ImmutableArray<Diagnostic> Diagnostics) send in sends )
        {
            _pending.Enqueue(send);
        }

        if ( !_draining && _pending.Count > 0 )
        {
            _draining = true;
            _ = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        while ( true )
        {
            lock ( _gate )
            {
                for ( int sent = 0; sent < BatchSends && _pending.Count > 0; sent++ )
                {
                    Send(_pending.Dequeue());
                }

                if ( _pending.Count == 0 )
                {
                    _draining = false;
                    return;
                }
            }

            await Task.Delay(BatchPause).ConfigureAwait(false);
        }
    }

    /// <summary>Completes once nothing is left queued — for a test that has to see a paced push end.</summary>
    internal async Task WhenSentAsync()
    {
        while ( true )
        {
            lock ( _gate )
            {
                if ( !_draining )
                {
                    return;
                }
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }

    private void Send((string Path, ImmutableArray<Diagnostic> Diagnostics) send)
    {
        if ( send.Diagnostics.IsDefault )
        {
            _publisher.Clear(send.Path);
        }
        else
        {
            _publisher.Publish(send.Path, version: null, send.Diagnostics);
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

        ImmutableArray<Diagnostic> shown = ForClosedFile(record.Diagnostics);
        if ( shown.IsEmpty )
        {
            return;
        }

        lock ( _gate )
        {
            Dispatch([(record.Path, shown)]);
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
            // A send still queued for this file would land after the sync handler's own publish and
            // replace the richer set with this one, so it is dropped; the take-back goes out now,
            // ahead of anything the sync handler sends.
            if ( _pending.Count > 0 )
            {
                List<(string Path, ImmutableArray<Diagnostic> Diagnostics)> kept = [.. _pending.Where(send => send.Path != key)];
                _pending.Clear();
                foreach ( (string Path, ImmutableArray<Diagnostic> Diagnostics) send in kept )
                {
                    _pending.Enqueue(send);
                }
            }

            if ( !_published.Remove(key) )
            {
                return;
            }

            _publisher.Clear(key);
        }
    }
}
