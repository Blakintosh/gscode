using System.Collections.Concurrent;
using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using FileSystemWatcher = OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher;

namespace GSCode.Server.Handlers;

/// <summary>
/// Applies workspace file create/change/delete events to the database. Editor buffers are the
/// source of truth for open files, so this skips them.
///
/// A branch switch sends thousands of events in one notification — VS Code's client batches every
/// event until 250 ms of quiet — and applying them used to happen inside the handler, one file at a
/// time: 5,000 changed files at 50,000 heavy BO3 files took 38 s, and a request asked meanwhile waited
/// up to 28.7 s for it. So the handler only queues a batch and returns. One pump at a time applies the
/// queue in arrival order, each path's last event standing for all of its earlier ones, and re-indexes
/// the changed scripts in parallel on threads below normal priority, as the lint sweep runs.
/// </summary>
public sealed class WatchedFilesHandler : DidChangeWatchedFilesHandlerBase
{
    private readonly WatchedFileUpdater _updater;
    private readonly ScriptDatabase _database;
    private readonly DocumentStore _documents;
    private readonly DependentDiagnosticsRefresher _dependents;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;
    private readonly WorkspaceLintSweep _lintSweep;
    private readonly ServerSettings _settings;

    private readonly Lock _gate = new();

    /// <summary>Events not yet applied, by normalized path, in the order each path first arrived.</summary>
    private Dictionary<string, PendingChange> _pending = new(StringComparer.Ordinal);

    /// <summary>The pump applying the queue, or a completed task when nothing is queued.</summary>
    private Task _pump = Task.CompletedTask;

    private bool _pumping;

    /// <summary>One path's change, as it stands after every event the queue has seen for it.</summary>
    private sealed record PendingChange(string Path, WatchedFileChange Change);

    public WatchedFilesHandler(
        WatchedFileUpdater updater,
        ScriptDatabase database,
        DocumentStore documents,
        DependentDiagnosticsRefresher dependents,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        WorkspaceLintSweep lintSweep,
        ServerSettings settings)
    {
        _lintSweep = lintSweep;
        _settings = settings;
        _updater = updater;
        _database = database;
        _documents = documents;
        _dependents = dependents;
        _workspaceDiagnostics = workspaceDiagnostics;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        // GlobPattern's implicit string conversion trips a nullable false-positive here.
#pragma warning disable CS8601
        FileSystemWatcher[] watchers =
        [
            .. GameProfile.Active.ScriptGlobs.Select(glob =>
                new FileSystemWatcher { GlobPattern = "**/" + glob }),
        ];
#pragma warning restore CS8601

        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<FileSystemWatcher>(watchers),
        };
    }

    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        lock ( _gate )
        {
            foreach ( FileEvent change in request.Changes )
            {
                WatchedFileChange kind = change.Type switch
                {
                    FileChangeType.Created => WatchedFileChange.Created,
                    FileChangeType.Deleted => WatchedFileChange.Deleted,
                    _ => WatchedFileChange.Changed,
                };

                string path;
                try
                {
                    path = change.Uri.GetFileSystemPath();
                }
                catch ( Exception exception )
                {
                    Log.Error(exception, "Failed to apply watched-file change for {Uri}", change.Uri);
                    continue;
                }

                // A file created and then written before the queue reached it is still a file that
                // appeared: applying it as a change would skip what an appearance does — retiring the
                // devraw copy at its path.
                string key = PathUtil.NormalizeAbsolute(path);
                if ( kind == WatchedFileChange.Changed
                    && _pending.TryGetValue(key, out PendingChange? earlier) && earlier.Change == WatchedFileChange.Created )
                {
                    kind = WatchedFileChange.Created;
                }

                _pending[key] = new PendingChange(path, kind);
            }

            if ( !_pumping && _pending.Count > 0 )
            {
                _pumping = true;
                _pump = Task.Run(PumpAsync);
            }
        }

        return Unit.Task;
    }

    /// <summary>Completes once every change queued so far has been applied, for a caller that must see them.</summary>
    public Task WhenIdleAsync()
    {
        lock ( _gate )
        {
            return _pump.WaitAsync(CancellationToken.None);
        }
    }

    private async Task PumpAsync()
    {
        while ( true )
        {
            List<PendingChange> batch;
            lock ( _gate )
            {
                if ( _pending.Count == 0 )
                {
                    _pumping = false;
                    return;
                }

                batch = [.. _pending.Values];
                _pending = new Dictionary<string, PendingChange>(StringComparer.Ordinal);
            }

            try
            {
                await ApplyAsync(batch).ConfigureAwait(false);
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to apply {Count} watched-file change(s)", batch.Count);
            }
        }
    }

    private async Task ApplyAsync(List<PendingChange> batch)
    {
        long startedTicks = Stopwatch.GetTimestamp();
        int applied = 0;

        // Every record the batch rewrote — a changed file, and every file a changed header is
        // inserted into — and the files whose exports moved, which are what other files'
        // diagnostics are computed against.
        ConcurrentDictionary<string, byte> touched = new(StringComparer.Ordinal);
        ConcurrentBag<string> exportsMoved = [];

        // The editor's buffer wins for any open file the update would rewrite — the changed file
        // itself, and every dependent of a changed header. The text-sync handler analyses an open
        // document on open, change and save, so re-reading disk here would either duplicate that
        // work or, with unsaved edits, quietly replace its record with older content.
        bool OwnedByEditor(string candidate)
        {
            return _documents.IsOpen(candidate);
        }

        void ApplyOne(PendingChange change)
        {
            try
            {
                // Whether this change is one an OPEN file's diagnostics could notice. Read either
                // side of the update, the same test the edit path uses — a branch switch that
                // rewrites a hundred bodies moves no signature and needs no re-linting.
                ulong before = SignatureOf(change.Path);
                foreach ( string path in _updater.Apply(change.Path, change.Change, OwnedByEditor, invalidateResolution: false) )
                {
                    touched.TryAdd(path, 0);
                }

                if ( SignatureOf(change.Path) != before )
                {
                    exportsMoved.Add(PathUtil.NormalizeAbsolute(change.Path));
                }

                Interlocked.Increment(ref applied);
            }
            catch ( Exception exception )
            {
                Log.Error(exception, "Failed to apply watched-file change for {Path}", change.Path);
            }
        }

        // Once for the batch rather than once per event: whether a file EXISTS is what a resolution
        // answer can be falsified by, and that is settled before anything below re-indexes.
        if ( batch.Any(change => change.Change != WatchedFileChange.Changed) )
        {
            _updater.NoteFilesAppearedOrVanished();
        }

        // Deletions first and headers last, one at a time; the scripts between them in parallel.
        // A header re-indexes every file inserting it — in parallel itself — from disk, so it reads
        // whatever this batch has already written; and a deletion is cheap and may retire a record
        // another change in the batch would otherwise resolve against.
        List<PendingChange> scripts = [];
        List<PendingChange> headers = [];
        foreach ( PendingChange change in batch )
        {
            if ( ScriptAnalysis.LanguageFromPath(change.Path) == ScriptLanguage.Gsh )
            {
                headers.Add(change);
            }
            else if ( change.Change == WatchedFileChange.Deleted )
            {
                ApplyOne(change);
            }
            else
            {
                scripts.Add(change);
            }
        }

        await RunBelowNormalAsync(scripts.Count, index => ApplyOne(scripts[index])).ConfigureAwait(false);

        foreach ( PendingChange change in headers )
        {
            ApplyOne(change);
        }

        // A re-index stores the parse diagnostics alone. In full mode a closed file reports its
        // cross-file problems too, so every record the update rewrote is linted again before it is
        // published — a branch switch otherwise emptied the Problems panel of every file it touched.
        if ( !touched.IsEmpty && _settings.IndexingMode == IndexingMode.Full && _database.HasCompletedIndex )
        {
            try
            {
                await _lintSweep.RelintClosedFilesAsync([.. touched.Keys], CancellationToken.None).ConfigureAwait(false);
            }
            catch ( OperationCanceledException )
            {
                // The next change, or the next start, lints these again.
            }
        }

        // Closed files carry their own stored diagnostics, which the update above just recomputed;
        // nothing was republishing them, so a file fixed on disk kept showing its old problems.
        // Cheap: this republishes what is already stored rather than re-analysing anything.
        if ( applied > 0 )
        {
            _workspaceDiagnostics.Refresh();
        }

        // Open files are computed against the changed ones, so every one of them is a dependent.
        // Naming each changed file as an origin also re-lints, in full mode, the CLOSED files that
        // call its functions. A deleted file has no record left to read its functions from, so its
        // closed callers wait for the next change or start; the open ones are still refreshed.
        foreach ( string origin in exportsMoved )
        {
            _dependents.Schedule(origin);
        }

        // A branch switch is the one time this runs for long, and nothing else says when it ended.
        Log.Debug(
            "Watched files: {Changes} change(s) applied in {Seconds:F1}s, {Touched} record(s) rewritten, {Moved} with moved exports",
            batch.Count, Stopwatch.GetElapsedTime(startedTicks).TotalSeconds, touched.Count, exportsMoved.Count);
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every index below <paramref name="count"/> across threads of
    /// their own at below-normal priority — the lint sweep's arrangement, for the same reason: this is
    /// background work, and the requests the editor is waiting on should win every core it wants.
    /// </summary>
    private static Task RunBelowNormalAsync(int count, Action<int> body)
    {
        if ( count == 0 )
        {
            return Task.CompletedTask;
        }

        int workers = Math.Min(count, Math.Max(1, Environment.ProcessorCount - 1));
        int next = -1;
        int running = workers;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Work()
        {
            try
            {
                int index;
                while ( (index = Interlocked.Increment(ref next)) < count )
                {
                    body(index);
                }
            }
            finally
            {
                if ( Interlocked.Decrement(ref running) == 0 )
                {
                    finished.SetResult();
                }
            }
        }

        for ( int worker = 0; worker < workers; worker++ )
        {
            Thread thread = new(Work)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "GSCode watched files",
            };
            thread.Start();
        }

        return finished.Task;
    }

    /// <summary>The file's export signature, or 0 when it is not (or no longer) indexed.</summary>
    private ulong SignatureOf(string path)
    {
        return _database.TryGetAnyRecord(path, out ScriptRecord record) ? ExportSignature.Of(record) : 0;
    }
}
