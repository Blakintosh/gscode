using System.Collections.Immutable;
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
/// diagnostics can be invalidated by an edit in a different file. Nothing pushed them, so removing
/// a <c>#namespace</c> left every caller squiggle-free until each was reopened, and adding the
/// missing <c>#using</c> left the warning sitting there after it was fixed.
///
/// Code lenses already had this problem and solved it by asking the client to re-request
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
/// CLOSED files were exempt for the same reason their diagnostics stayed parse-level-only — see
/// <c>ScriptDatabase.HasCompletedLintSweep</c> — until <c>workspaceIndexingMode: full</c> gave them
/// cross-file diagnostics too. That case is narrow BY NAME rather than wide the way the open-tab
/// scope is: <see cref="RefreshClosedDependentsAsync"/> uses <c>LanguageStore.FilesReferencing</c>
/// on the origin's own declared functions, so a rename costs the files that mention the name
/// rather than the workspace — affordable specifically because it does NOT try to be the open-tab
/// scope's "every one, since there are few" answer at workspace scale.
///
/// It only fires for a caller-named origin path — an on-disk change
/// with no single origin (a branch switch, most plausibly) does not attempt it, and neither does
/// a class rename or a method edit (only top-level function declarations are covered so far). Both are stated gaps, not
/// silent ones: the closed file involved keeps whatever cross-file diagnostics its last sweep or
/// refresh gave it until the next full sweep, an edit that does reach it through
/// <see cref="RefreshClosedDependentsAsync"/>, or <c>gscode.clearCacheAndReindex</c>.
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
    /// One debounce, many origins. It used to be one origin and one token source, so a refresh
    /// scheduled for file A was cancelled outright by one scheduled for file B a moment later and
    /// A's dependents were never refreshed at all — a burst of edits across two files left the
    /// first file's callers showing diagnostics computed against exports it no longer has. A token
    /// source per origin would mean a timer per origin; accumulating costs one set.
    /// </summary>
    private readonly HashSet<string> _origins = new(StringComparer.Ordinal);

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
    public void Schedule(string originPath = "")
    {
        Restart([originPath]);
    }

    /// <summary>
    /// Adds origins to the waiting set and restarts the debounce: the pass in flight is cancelled
    /// and a new one takes over everything waiting, including whatever the cancelled one had left.
    /// </summary>
    private void Restart(IReadOnlyCollection<string> origins)
    {
        CancellationTokenSource pending = new();

        lock ( _gate )
        {
            foreach ( string originPath in origins )
            {
                _origins.Add(originPath);
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
        HashSet<string> origins;
        lock ( _gate )
        {
            origins = new HashSet<string>(_origins, StringComparer.Ordinal);
            _origins.Clear();
        }

        // What this pass has not finished with. Cancellation after the take is the case the
        // comment above did NOT cover: the set has already been cleared, so an origin abandoned
        // mid-Refresh, or part-way down the loop below, was known to nobody. Nothing else
        // re-lints a CLOSED dependent, so that file kept diagnostics computed against exports
        // the origin no longer has until the next unrelated edit happened to name it.
        HashSet<string> outstanding = new(origins, StringComparer.Ordinal);

        try
        {
            // Before any of the work, not only inside the loops. A pass superseded while it
            // waited for the lock has nothing to contribute, and an empty workspace would
            // otherwise reach the end of both loops without ever consulting the token.
            cancellationToken.ThrowIfCancellationRequested();

            Refresh(origins, cancellationToken);

            foreach ( string originPath in origins )
            {
                await RefreshClosedDependentsAsync(originPath, cancellationToken).ConfigureAwait(false);
                outstanding.Remove(originPath);
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
                return new HashSet<string>(_origins, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Asks the client to re-request its code lenses, once per coalesced pass.
    ///
    /// A lens count depends on every file that references the symbol, which the client has no way
    /// to know changed, so editing file A never re-requested the lenses shown in file B. It used to
    /// be sent from <c>TextSyncHandler</c> per ANALYSIS and undebounced — typing a function's name
    /// moves the export signature on every keystroke, so a client with lenses on re-requested them
    /// about four times a second, and one such request measured 164 ms on the densest cod4 script
    /// (<c>HandlerCostTests</c>). Here it rides the fan-out that already coalesces this exact
    /// event, and it also covers the on-disk-change case, which the sync handler could not see.
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
    private void ReturnOrigins(IReadOnlySet<string> outstanding)
    {
        if ( outstanding.Count == 0 )
        {
            return;
        }

        Restart(outstanding);
    }

    /// <summary>
    /// The `full`-mode half of a refresh: CLOSED files that reference a function the origin
    /// declares. See the class comment for exactly what this does and does not cover yet.
    /// </summary>
    private async Task RefreshClosedDependentsAsync(string originPath, CancellationToken cancellationToken)
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

        HashSet<string> dependents = ClosedDependentsOf(origin, _database.StoreFor(origin.Language), _documents);
        if ( dependents.Count == 0 )
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        LintSweepOutcome outcome = await _lintSweep.RelintClosedFilesAsync(dependents, cancellationToken)
            .ConfigureAwait(false);

        if ( outcome.Linted > 0 )
        {
            // SetDiagnostics only changed what a record HOLDS; nothing has told the client yet.
            _workspaceDiagnostics.Refresh();

            Log.Verbose(
                "Re-linted {Count} closed dependent(s) in {Elapsed:F1}ms after {Path} changed its exports",
                outcome.Linted,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                originPath);
        }
    }

    /// <summary>Verbose document listing caps here — enough to name the tabs without flooding the log.</summary>
    private const int MaxLoggedDocuments = 5;

    private void Refresh(IReadOnlySet<string> origins, CancellationToken cancellationToken)
    {
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        List<string> refreshedPaths = new();

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
                System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
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
    /// <c>StartupIndex</c>), not a second unlabelled case.
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
    /// The CLOSED files that reference any function <paramref name="origin"/> declares — the
    /// dependent set a full-mode re-lint actually needs to touch. Excludes the origin itself and
    /// any path that turns out to be open (the live-analysis path already covers those, from
    /// text that may be ahead of what is on disk).
    ///
    /// Only function declarations, not classes — a stated gap, not a silent one; see the class
    /// comment.
    /// </summary>
    internal static HashSet<string> ClosedDependentsOf(ScriptRecord origin, LanguageStore store, DocumentStore documents)
    {
        HashSet<string> dependents = new(StringComparer.Ordinal);

        // KeyNamespace, not the declared namespace: on a merge dialect a function is declared into
        // the file stem but every call to it is keyed with no namespace, so a key rebuilt from the
        // declaration matched no reference and no closed caller was ever re-linted.
        GameProfile game = GameProfile.Active;

        // Top-level functions only: a class method lives on its class, not in this list, so methods
        // fall inside the class gap the class comment states rather than being covered here.
        foreach ( FunctionSymbol function in origin.Functions )
        {
            SymbolKey key = new(game.KeyNamespace(function.Namespace), function.KeyName, SymbolKind.Function);

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
