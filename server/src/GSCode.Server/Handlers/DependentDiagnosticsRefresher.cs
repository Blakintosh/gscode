using System.Collections.Immutable;
using System.Diagnostics;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using Serilog;
using GSCode.Server.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.Server.Handlers;

/// <summary>
/// Re-lints the OTHER open documents when an edit changes something they can see.
///
/// The cross-file lints read their neighbours — whether a <c>#using</c> supplies a namespace,
/// whether a called function exists, is private, or takes that many arguments — so a file's
/// diagnostics can be invalidated by an edit in a different file. Unpushed, removing a
/// <c>#namespace</c> would leave every caller squiggle-free until each was reopened, and adding the
/// missing <c>#using</c> would leave its warning in place after the fix.
///
/// Code lenses have the same problem and solve it by asking the client to re-request
/// (<c>workspace/codeLens/refresh</c>). Diagnostics are server-PUSHED, so there is no equivalent
/// to ask for: the server has to republish them itself.
///
/// Two things keep this affordable, and both matter:
///
/// 1. It runs only when the edited file's <see cref="ExportSignature"/> moves. Typing inside a
///    function body — very nearly every keystroke — changes nothing another file can observe, and
///    is skipped outright.
/// 2. A dependent's TEXT has not changed, so its parse is reused (<c>AnalyzeIfStale</c> returns the
///    cached result) and only the lints re-run. Revalidation costs a lint pass, not a re-parse.
///    The one exception is a changed <c>#insert</c>ed header, which invalidates the parse itself —
///    the macro bodies it expanded are no longer what the header says — and <c>AnalyzeIfStale</c>
///    re-parses for that reason alone. Header edits are rare and user-paced, so the exception does
///    not touch the typing path.
///
/// Scope for OPEN documents is every OTHER one, not a computed dependency set — they are few (the
/// user's tabs), while "reaches this file" is not a simple question: under the merge dialects an
/// unqualified call resolves by name across the whole workspace, so a narrow answer would be wrong
/// rather than merely conservative.
///
/// CLOSED files have cross-file diagnostics only under <c>workspaceIndexingMode: full</c> (see
/// <c>ScriptDatabase.HasCompletedLintSweep</c>). That case is narrow BY NAME rather than wide the
/// way the open-tab scope is: <see cref="RefreshClosedDependentsAsync"/> uses
/// <c>LanguageStore.FilesReferencing</c> on the origin's own declared functions, so a rename costs
/// the files that mention the name rather than the workspace — affordable specifically because it
/// does NOT try to be the open-tab scope's "every one, since there are few" answer at workspace
/// scale.
///
/// It only fires for a caller-named origin path — an on-disk change with no single origin (a branch
/// switch, most plausibly) does not attempt it — and covers top-level function declarations only,
/// not a class rename or a method edit. Both are stated gaps, not silent ones: the closed file
/// involved keeps whatever cross-file diagnostics its last sweep or refresh gave it until the next
/// full sweep, an edit that does reach it through <see cref="RefreshClosedDependentsAsync"/>, or
/// <c>gscode.clearCacheAndReindex</c>.
/// </summary>
public sealed class DependentDiagnosticsRefresher
{
    /// <summary>
    /// Longer than the per-document debounce, and coalesced across edits. Typing a new function's
    /// name changes the signature on EVERY keystroke, so a fan-out per change would re-lint every
    /// open tab per character; waiting for the name to be finished collapses that to one pass.
    /// </summary>
    private const int DebounceMilliseconds = 900;

    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly DocumentLinter _linter;
    private readonly ScriptDatabase _database;
    private readonly WorkspaceLintSweep _lintSweep;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;
    private readonly ICodeLensRefreshSink _codeLenses;
    private readonly ServerSettings _settings;

    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Every origin scheduled since the last pass ran, taken and cleared when it does.
    ///
    /// One debounce, many origins. One origin and one token source would let a refresh scheduled for
    /// file B cancel file A's outright, leaving A's callers showing diagnostics computed against
    /// exports it no longer has. A token source per origin would mean a timer per origin;
    /// accumulating costs one set.
    /// </summary>
    ///
    /// Each origin carries the functions its edits changed, or null when they cannot be named — an
    /// on-disk change, a saved header, a moved file. Null wins a merge: one wide schedule means every
    /// caller of the origin is re-linted.
    private readonly Dictionary<string, HashSet<SymbolKey>?> _origins = new(StringComparer.Ordinal);

    public DependentDiagnosticsRefresher(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        DocumentLinter linter,
        ScriptDatabase database,
        WorkspaceLintSweep lintSweep,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        ILanguageServerFacade server,
        ServerSettings settings)
        : this(
            documents,
            diagnostics,
            linter,
            database,
            lintSweep,
            workspaceDiagnostics,
            new LanguageServerCodeLensRefreshSink(server),
            settings)
    {
    }

    internal DependentDiagnosticsRefresher(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        DocumentLinter linter,
        ScriptDatabase database,
        WorkspaceLintSweep lintSweep,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        ICodeLensRefreshSink codeLenses,
        ServerSettings settings)
    {
        _codeLenses = codeLenses;
        _settings = settings;
        _documents = documents;
        _diagnostics = diagnostics;
        _linter = linter;
        _database = database;
        _lintSweep = lintSweep;
        _workspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Queues a refresh of every open document except <paramref name="originPath"/>, which the
    /// caller is already publishing for. Restarts the wait; the origin joins the ones already
    /// waiting rather than replacing them.
    /// </summary>
    /// <param name="originPath">
    /// The document the caller publishes itself, or "" when there is none — an on-disk change
    /// behind the editor's back belongs to no open document, so every one of them is a dependent.
    /// </param>
    /// <param name="changedFunctions">
    /// The function keys the origin's edit added, removed or re-signed — see
    /// <see cref="ExportSignature.ChangedFunctionKeys"/> — so the closed re-lint reaches only their
    /// callers. Null when they cannot be named, which re-lints every closed caller of the origin.
    /// </param>
    public void Schedule(string originPath = "", IReadOnlySet<SymbolKey>? changedFunctions = null)
    {
        Restart([new KeyValuePair<string, IReadOnlySet<SymbolKey>?>(originPath, changedFunctions)]);
    }

    /// <summary>
    /// Adds origins to the waiting set and restarts the debounce: the pass in flight is cancelled
    /// and a new one takes over everything waiting, including whatever the cancelled one had left.
    /// </summary>
    private void Restart(IEnumerable<KeyValuePair<string, IReadOnlySet<SymbolKey>?>> origins)
    {
        CancellationTokenSource pending = new();

        lock ( _gate )
        {
            foreach ( KeyValuePair<string, IReadOnlySet<SymbolKey>?> origin in origins )
            {
                Merge(origin.Key, origin.Value);
            }

            _pending?.Cancel();
            _pending = pending;
        }

        _ = RunAsync(pending.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            await RunPassAsync(cancellationToken).ConfigureAwait(false);
        }
        catch ( OperationCanceledException )
        {
            // Superseded by a later edit — the newer pass covers this one, and now covers whatever
            // this one had left.
        }
        catch ( Exception exception )
        {
            Log.Error(exception, "Dependent diagnostics refresh failed");
        }
    }

    /// <summary>
    /// One fan-out: take the origins scheduled since the last pass, re-lint the open documents that
    /// can see them, then the closed dependents of each.
    ///
    /// Separate from the debounce above so a test can run a pass without waiting 900 ms for one.
    /// </summary>
    internal async Task RunPassAsync(CancellationToken cancellationToken)
    {
        // Taken after the wait, not before it: everything scheduled during the window belongs
        // to this pass. A pass cancelled DURING the wait never gets here, so its origins stay in
        // the set and the pass that superseded it picks them up.
        Dictionary<string, HashSet<SymbolKey>?> taken;
        lock ( _gate )
        {
            taken = new Dictionary<string, HashSet<SymbolKey>?>(_origins, StringComparer.Ordinal);
            _origins.Clear();
        }

        HashSet<string> origins = new(taken.Keys, StringComparer.Ordinal);

        // What this pass has not finished with. Cancellation after the take would otherwise lose it:
        // the set has already been cleared, and nothing else re-lints a CLOSED dependent, which would
        // keep diagnostics computed against exports the origin no longer has.
        Dictionary<string, HashSet<SymbolKey>?> outstanding = new(taken, StringComparer.Ordinal);

        try
        {
            // Before any of the work, not only inside the loops. A pass superseded while it
            // waited for the lock has nothing to contribute, and an empty workspace would
            // otherwise reach the end of both loops without ever consulting the token.
            cancellationToken.ThrowIfCancellationRequested();

            Refresh(origins, cancellationToken);

            foreach ( KeyValuePair<string, HashSet<SymbolKey>?> origin in taken )
            {
                await RefreshClosedDependentsAsync(origin.Key, origin.Value, cancellationToken).ConfigureAwait(false);
                outstanding.Remove(origin.Key);
            }

            RequestCodeLensRefresh();
        }
        catch ( OperationCanceledException )
        {
            // Cancellation only. A pass that FAILED is a defect rather than a supersession, and
            // handing its origins back would reschedule the same failing pass every 900 ms for
            // the rest of the session; RunAsync logs it instead.
            ReturnOrigins(outstanding);
            throw;
        }
    }

    /// <summary>The origins waiting for the next pass. For tests; the live set is private.</summary>
    internal IReadOnlySet<string> PendingOrigins
    {
        get
        {
            lock ( _gate )
            {
                return new HashSet<string>(_origins.Keys, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// The functions an origin waiting for the next pass changed, or null when it waits wide (or
    /// not at all). For tests.
    /// </summary>
    internal IReadOnlySet<SymbolKey>? PendingChangedFunctions(string originPath)
    {
        lock ( _gate )
        {
            return _origins.TryGetValue(originPath, out HashSet<SymbolKey>? keys) && keys is not null
                ? new HashSet<SymbolKey>(keys)
                : null;
        }
    }

    /// <summary>
    /// Adds one origin's changed functions to what already waits for it. Null — "every caller" —
    /// absorbs anything merged with it. Called under <see cref="_gate"/>.
    /// </summary>
    private void Merge(string originPath, IReadOnlySet<SymbolKey>? changedFunctions)
    {
        if ( !_origins.TryGetValue(originPath, out HashSet<SymbolKey>? waiting) )
        {
            _origins[originPath] = changedFunctions is null ? null : new HashSet<SymbolKey>(changedFunctions);
            return;
        }

        if ( waiting is null )
        {
            return;
        }

        if ( changedFunctions is null )
        {
            _origins[originPath] = null;
            return;
        }

        waiting.UnionWith(changedFunctions);
    }

    /// <summary>
    /// Asks the client to re-request its code lenses, once per coalesced pass.
    ///
    /// A lens count depends on every file that references the symbol, which the client has no way
    /// to know changed. Once per coalesced pass rather than per analysis: typing a function's name
    /// moves the export signature on every keystroke, and sent per analysis the refresh had a client
    /// re-requesting about four times a second, at 164 ms on the densest cod4 script
    /// (<c>HandlerCostTests</c>). Riding this fan-out also covers the on-disk-change case.
    /// </summary>
    private void RequestCodeLensRefresh()
    {
        if ( !_settings.CodeLensEnabled )
        {
            return;
        }

        _codeLenses.Request();
    }

    /// <summary>
    /// Puts origins this pass did not finish with back in the queue.
    ///
    /// Scheduled rather than merely added: the pass that superseded this one may already be past
    /// its own take, in which case nothing would come along to look at the set again.
    /// </summary>
    private void ReturnOrigins(Dictionary<string, HashSet<SymbolKey>?> outstanding)
    {
        if ( outstanding.Count == 0 )
        {
            return;
        }

        List<KeyValuePair<string, IReadOnlySet<SymbolKey>?>> returned = [];
        foreach ( KeyValuePair<string, HashSet<SymbolKey>?> origin in outstanding )
        {
            returned.Add(new KeyValuePair<string, IReadOnlySet<SymbolKey>?>(origin.Key, origin.Value));
        }

        Restart(returned);
    }

    /// <summary>
    /// The `full`-mode half of a refresh: CLOSED files that call what the origin's edit changed —
    /// <paramref name="changedFunctions"/>, or every function it declares when that is null. See the
    /// class comment for exactly what this does and does not cover yet.
    /// </summary>
    private async Task RefreshClosedDependentsAsync(
        string originPath, IReadOnlySet<SymbolKey>? changedFunctions, CancellationToken cancellationToken)
    {
        if ( originPath.Length == 0 || !_database.HasCompletedLintSweep )
        {
            return;
        }

        if ( !_database.TryGetAnyRecord(originPath, out ScriptRecord origin)
            || origin.Language is not (ScriptLanguage.Gsc or ScriptLanguage.Csc) )
        {
            return;
        }

        HashSet<string> dependents = ClosedDependentsOf(origin, _database.StoreFor(origin.Language), _documents, changedFunctions);
        if ( dependents.Count == 0 )
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        long startedTicks = Stopwatch.GetTimestamp();
        LintSweepOutcome outcome = await _lintSweep.RelintClosedFilesAsync(dependents, cancellationToken)
            .ConfigureAwait(false);

        if ( outcome.Linted > 0 )
        {
            // SetDiagnostics only changed what a record HOLDS; nothing has told the client yet.
            _workspaceDiagnostics.Refresh();

            Log.Verbose(
                "Re-linted {Count} closed dependent(s) in {Elapsed:F1}ms after {Path} changed its exports",
                outcome.Linted,
                Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                originPath);
        }
    }

    /// <summary>Verbose document listing caps here — enough to name the tabs without flooding the log.</summary>
    private const int MaxLoggedDocuments = 5;

    private void Refresh(IReadOnlySet<string> origins, CancellationToken cancellationToken)
    {
        long startedTicks = Stopwatch.GetTimestamp();
        List<string> refreshedPaths = [];

        foreach ( OpenDocument document in _documents.OpenDocuments )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ( !ShouldRefresh(document, origins) )
            {
                continue;
            }

            RefreshOne(document);
            refreshedPaths.Add(document.Path);
        }

        if ( refreshedPaths.Count > 0 )
        {
            Log.Verbose(
                "Re-linted {Count} open document(s) in {Elapsed:F1}ms after {Origins} changed their exports: {Documents}",
                refreshedPaths.Count,
                Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                DescribeOrigins(origins),
                DescribeDocuments(refreshedPaths));
        }
    }

    /// <summary>
    /// Names what triggered this pass. Origins carries "" for an on-disk change with no editor
    /// caller (see <see cref="Schedule"/>) — left unnamed, that printed as a blank in the log
    /// ("after  changed its exports"), which read like a missing value rather than the documented
    /// no-origin case. Startup's own no-origin <c>Schedule()</c> call, once indexing completes,
    /// prints the same "on-disk change" label — the reasoning is identical (see the call site in
    /// <c>StartupIndexRunner</c>), not a second unlabelled case.
    /// </summary>
    private static string DescribeOrigins(IReadOnlySet<string> origins)
    {
        List<string> named = origins.Where(static origin => origin.Length > 0).ToList();
        bool diskChange = origins.Contains("");

        if ( named.Count == 0 )
        {
            return "an on-disk change";
        }

        string joined = string.Join(", ", named);
        return diskChange ? $"{joined} and an on-disk change" : joined;
    }

    /// <summary>Which documents were re-linted, capped at <see cref="MaxLoggedDocuments"/>.</summary>
    private static string DescribeDocuments(IReadOnlyList<string> paths)
    {
        if ( paths.Count <= MaxLoggedDocuments )
        {
            return string.Join(", ", paths);
        }

        return string.Join(", ", paths.Take(MaxLoggedDocuments)) + $", and {paths.Count - MaxLoggedDocuments} more";
    }

    /// <summary>
    /// Whether one open document needs re-linting, given the origins a coalesced pass is refreshing
    /// for. A document is refreshed unless it is one of them.
    /// </summary>
    /// <remarks>
    /// Two exclusions, for opposite reasons. An ORIGIN is already being published by the handler
    /// that ran its edit, so refreshing it here would only race that. A STALE document has text
    /// newer than anything committed and a debounced analysis of its own already queued — that pass
    /// runs after this one and against the same database, so it produces the same answer; doing it
    /// here as well would publish diagnostics for text the user has already replaced.
    /// </remarks>
    internal static bool ShouldRefresh(OpenDocument document, IReadOnlySet<string> origins)
    {
        return !origins.Contains(document.Path) && !document.IsStale;
    }

    /// <summary>
    /// The CLOSED files that call what the origin's edit changed — the dependent set a full-mode
    /// re-lint actually needs to touch. Excludes the origin itself and any path that turns out to be
    /// open (the live-analysis path already covers those, from text that may be ahead of what is on
    /// disk).
    ///
    /// With <paramref name="changedFunctions"/>, the callers of those keys only: adding one function
    /// to a shared utility can change what a call to IT reports and nothing else, and re-linting
    /// every caller of the file re-parsed 263 closed scripts on stock BO3 for an edit none of them
    /// could see — about 300 MB of allocation in a third of a second, which the GC then kept
    /// committed. A removed function's key is in the set too, so its callers are found although the
    /// origin no longer declares it. Without the set, every function the origin declares.
    ///
    /// Only function declarations, not classes — a stated gap, not a silent one; see the class
    /// comment.
    /// </summary>
    internal static HashSet<string> ClosedDependentsOf(
        ScriptRecord origin, LanguageStore store, DocumentStore documents, IReadOnlySet<SymbolKey>? changedFunctions = null)
    {
        HashSet<string> dependents = new(StringComparer.Ordinal);
        foreach ( SymbolKey key in changedFunctions ?? FunctionKeysOf(origin) )
        {
            foreach ( string path in store.FilesReferencing(key) )
            {
                if ( string.Equals(path, origin.Path, StringComparison.Ordinal) )
                {
                    continue;
                }

                if ( documents.IsOpen(path) )
                {
                    continue;
                }

                dependents.Add(path);
            }
        }

        return dependents;
    }

    /// <summary>
    /// Every top-level function <paramref name="origin"/> declares, under the key its callers'
    /// references were indexed with. KeyNamespace, not the declared namespace: on a merge dialect a
    /// function is declared into the file stem but every call to it is keyed with no namespace, so a
    /// key rebuilt from the declaration matched no reference and no closed caller was ever re-linted.
    /// A class method lives on its class, not in this list, so methods fall inside the class gap the
    /// class comment states.
    /// </summary>
    private static HashSet<SymbolKey> FunctionKeysOf(ScriptRecord origin)
    {
        GameProfile game = GameProfile.Active;
        HashSet<SymbolKey> keys = [];
        foreach ( FunctionSymbol function in origin.Functions )
        {
            keys.Add(new SymbolKey(game.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function));
        }

        return keys;
    }

    internal void RefreshOne(OpenDocument document)
    {
        // Reuses the cached parse — the text has not changed, only the world around it.
        //
        // The SNAPSHOT's version, not document.Version read afterwards. This is the same defect the
        // sync handler was fixed for: an edit landing while this analysis runs would stamp a parse
        // of the older text with the newest version, telling the client a stale set describes text
        // it has already moved past. The staleness check in ShouldRefresh narrows the window but
        // cannot close it — it is checked before the analysis, and the edit arrives during it.
        AnalysisSnapshot snapshot = _documents.AnalyzeSnapshotIfStale(document);

        ImmutableArray<Diagnostic> diagnostics = _linter.Analyze(document, snapshot.Result);

        _diagnostics.Publish(document.Path, snapshot.Version, diagnostics);
    }
}
