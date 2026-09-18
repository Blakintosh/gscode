using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Serilog;

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
/// a class rename (only function declarations are covered so far). Both are stated gaps, not
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

    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public DependentDiagnosticsRefresher(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        DocumentLinter linter,
        ScriptDatabase database,
        WorkspaceLintSweep lintSweep,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _linter = linter;
        _database = database;
        _lintSweep = lintSweep;
        _workspaceDiagnostics = workspaceDiagnostics;
    }

    /// <summary>
    /// Queues a refresh of every open document except <paramref name="originPath"/>, which the
    /// caller is already publishing for. Supersedes any refresh still waiting.
    /// </summary>
    /// <param name="originPath">
    /// The document the caller publishes itself, or "" when there is none — an on-disk change
    /// behind the editor's back belongs to no open document, so every one of them is a dependent.
    /// </param>
    public void Schedule(string originPath = "")
    {
        CancellationTokenSource pending = new();

        lock ( _gate )
        {
            _pending?.Cancel();
            _pending = pending;
        }

        _ = RunAsync(originPath, pending.Token);
    }

    private async Task RunAsync(string originPath, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            Refresh(originPath, cancellationToken);
            await RefreshClosedDependentsAsync(originPath, cancellationToken).ConfigureAwait(false);
        }
        catch ( OperationCanceledException )
        {
            // Superseded by a later edit — the newer pass covers this one.
        }
        catch ( Exception exception )
        {
            Log.Error(exception, "Dependent diagnostics refresh failed after {Path}", originPath);
        }
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

    private void Refresh(string originPath, CancellationToken cancellationToken)
    {
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        int refreshed = 0;

        foreach ( OpenDocument document in _documents.OpenDocuments )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ( !ShouldRefresh(document, originPath) )
            {
                continue;
            }

            RefreshOne(document);
            refreshed++;
        }

        if ( refreshed > 0 )
        {
            Log.Verbose(
                "Re-linted {Count} open document(s) in {Elapsed:F1}ms after {Path} changed its exports",
                refreshed,
                System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
                originPath);
        }
    }

    /// <summary>
    /// Whether one open document needs re-linting because <paramref name="originPath"/> changed.
    /// </summary>
    /// <remarks>
    /// Two exclusions, for opposite reasons. The ORIGIN is already being published by the handler
    /// that ran the edit, so refreshing it would only race that. A STALE document has text newer
    /// than anything committed and a debounced analysis of its own already queued — that pass runs
    /// after this one and against the same database, so it produces the same answer; doing it here
    /// as well would publish diagnostics for text the user has already replaced.
    /// </remarks>
    internal static bool ShouldRefresh(OpenDocument document, string originPath)
    {
        return !string.Equals(document.Path, originPath, StringComparison.Ordinal) && !document.IsStale;
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

        foreach ( FunctionSymbol function in origin.Functions )
        {
            SymbolKey key = function.OwnerClassKeyName is string ownerClass
                ? new SymbolKey(null, function.KeyName, SymbolKind.Function, ownerClass)
                : new SymbolKey(
                    function.Namespace.Length == 0 ? null : function.Namespace, function.KeyName, SymbolKind.Function);

            foreach ( string path in store.FilesReferencing(key) )
            {
                if ( string.Equals(path, origin.Path, StringComparison.Ordinal) )
                {
                    continue;
                }

                if ( documents.TryGet(path, out OpenDocument _) )
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
