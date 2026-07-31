using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
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
///
/// Scope is every OTHER open document, not a computed dependency set. Open documents are few — the
/// user's tabs — while "reaches this file" is not a simple question: under the merge dialects an
/// unqualified call resolves by name across the whole workspace, so a narrow answer would be wrong
/// rather than merely conservative. Closed files need nothing here, since their stored diagnostics
/// are parse-level and depend on no other file.
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
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;

    private readonly object _gate = new();
    private CancellationTokenSource? _pending;

    public DependentDiagnosticsRefresher(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        ScriptDatabase database,
        ResolverHolder resolver,
        BuiltinApiSet builtins,
        ObjectFields objectFields)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _database = database;
        _resolver = resolver;
        _builtins = builtins;
        _objectFields = objectFields;
    }

    /// <summary>
    /// Queues a refresh of every open document except <paramref name="originPath"/>, which the
    /// caller is already publishing for. Supersedes any refresh still waiting.
    /// </summary>
    public void Schedule(string originPath)
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

    private void RefreshOne(OpenDocument document)
    {
        // Reuses the cached parse — the text has not changed, only the world around it.
        ParseResult result = _documents.AnalyzeIfStale(document);

        ImmutableArray<Diagnostic> diagnostics = WorkspaceLints.Analyze(
            result, document.Language, document.Path, _database, _resolver.Current, _builtins, _objectFields);

        _diagnostics.Publish(DocumentUri.FromFileSystemPath(document.Path), document.Version, diagnostics);
    }
}
