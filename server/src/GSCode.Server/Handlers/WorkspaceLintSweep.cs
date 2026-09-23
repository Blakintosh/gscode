using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;

namespace GSCode.Server.Handlers;

/// <summary>Outcome of one lint sweep pass — logged the same way <c>IndexOutcome</c> is.</summary>
public readonly record struct LintSweepOutcome(int Linted, int Skipped, int Total);

/// <summary>
/// The <c>workspaceIndexingMode: full</c> feature: runs the cross-file lints over every indexed
/// GSC/CSC file, not just open ones, and stores the MERGED diagnostics on the record so a closed
/// file's Problems entry stops being parse-level-only. This is the item
/// <c>FOLLOWUPS.md</c>'s "Cross-file lints for files that are not open" deferred on a 44 ms/file
/// estimate that turned out to be the corpus sweep's own overhead, not this work — see
/// <c>PERF.md</c>'s 2026-09-15 entry for the real, much smaller number.
///
/// Deliberately does not retain a <see cref="ParseResult"/> anywhere: it re-reads and re-parses a
/// file (<see cref="WorkspaceIndexer.AnalyzeForLintSweep"/>), lints it, stores the diagnostics
/// through <see cref="ScriptDatabase.SetDiagnostics"/>, and drops the parse. Holding 1,105 parses
/// (BO3's stock tree) is exactly the memory the record-only design elsewhere in this codebase
/// avoids, and this pass has no reason to be the exception.
///
/// Two entry points share one per-file worker: <see cref="RunFullSweepAsync"/> for the startup
/// pass over everything, and <see cref="RelintClosedFilesAsync"/> for the narrow set an edit's
/// dependents name (see <see cref="DependentDiagnosticsRefresher"/>) — a rename costs the files
/// that mention the name, not the workspace, the same way the open-document path already works.
/// </summary>
public sealed class WorkspaceLintSweep
{
    private readonly ScriptDatabase _database;
    private readonly DocumentStore _documents;
    private readonly WorkspaceIndexer _indexer;
    private readonly DocumentLinter _linter;

    public WorkspaceLintSweep(
        ScriptDatabase database, DocumentStore documents, WorkspaceIndexer indexer, DocumentLinter linter)
    {
        _database = database;
        _documents = documents;
        _indexer = indexer;
        _linter = linter;
    }

    /// <summary>
    /// Lints every currently-indexed GSC/CSC record. Called once, after the startup index
    /// finishes — <see cref="ScriptDatabase.HasCompletedIndex"/> has to be true first, or the
    /// index-gated lints (5013/5014/5025/5026) would report every cross-file call as missing.
    /// </summary>
    public async Task<LintSweepOutcome> RunFullSweepAsync(CancellationToken cancellationToken)
    {
        List<ScriptRecord> targets = [.. _database.Gsc.AllRecords, .. _database.Csc.AllRecords];

        LintSweepOutcome outcome = await SweepAsync(targets, cancellationToken).ConfigureAwait(false);
        _database.MarkLintSweepComplete();
        return outcome;
    }

    /// <summary>
    /// Re-lints the CLOSED files named by <paramref name="paths"/> — the dependent set an edit's
    /// exported-symbol change actually reaches, computed by the caller via
    /// <see cref="LanguageStore.FilesReferencing"/>. A path that turns out to be open is skipped:
    /// the live analysis path already covers it with the richer, real-time result.
    /// </summary>
    public async Task<LintSweepOutcome> RelintClosedFilesAsync(
        IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        List<ScriptRecord> targets = [];
        foreach ( string path in paths )
        {
            if ( _database.TryGetAnyRecord(path, out ScriptRecord record)
                && record.Language is ScriptLanguage.Gsc or ScriptLanguage.Csc )
            {
                targets.Add(record);
            }
        }

        return await SweepAsync(targets, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LintSweepOutcome> SweepAsync(List<ScriptRecord> targets, CancellationToken cancellationToken)
    {
        int linted = 0;
        int skipped = 0;

        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(targets, options, (record, token) =>
        {
            token.ThrowIfCancellationRequested();

            if ( RelintOne(record) )
            {
                Interlocked.Increment(ref linted);
            }
            else
            {
                Interlocked.Increment(ref skipped);
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        return new LintSweepOutcome(linted, skipped, targets.Count);
    }

    /// <summary>
    /// Re-lints one record and stores the result. False when the file could not be re-read, or
    /// changed underneath the sweep — see <see cref="ScriptDatabase.SetDiagnostics"/>'s
    /// content-hash gate — either of which the caller counts as skipped rather than failed: both
    /// mean some OTHER path (an open document, a watched-file change, a later sweep) now owns the
    /// file's diagnostics.
    /// </summary>
    private bool RelintOne(ScriptRecord record)
    {
        // An open document's buffer is the source of truth and already carries the richer,
        // live-analysis diagnostics; reading disk here would describe text the user has replaced.
        if ( _documents.IsOpen(record.Path) )
        {
            return false;
        }

        ParseResult? result = _indexer.AnalyzeForLintSweep(record.Path);
        if ( result is null )
        {
            return false;
        }

        System.Collections.Immutable.ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics =
            _linter.Analyze(record.Language, record.Path, result);

        // The hash of what was JUST read and linted, not record.ContentHash as captured before
        // this ran: disk (and the live record, via a watched-file update) can move between the two,
        // and gating on the stale snapshot would let a sweep started against old content overwrite
        // a record a concurrent update already brought current — silently regressing it back to
        // describing text that no longer exists. Gating on the fresh hash makes the two agree
        // whenever they legitimately can, and decline whenever they cannot.
        ulong freshContentHash = ScriptDatabase.ComputeContentHash(result.Text.Text);
        return _database.SetDiagnostics(record.Path, record.Language, freshContentHash, diagnostics);
    }
}
