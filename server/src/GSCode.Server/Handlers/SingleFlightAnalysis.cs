using System.Collections.Concurrent;
using GSCode.Workspace.Documents;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Runs a document's analysis, coalescing with whatever is already running for it — see
/// <see cref="AnalysisGate"/>. If nothing is running, runs (and re-runs, if a request arrived
/// while it was busy) here and now; otherwise queues the rerun and returns immediately, leaving
/// the in-flight call to pick it up.
///
/// Pulled out of <see cref="TextSyncHandler"/> as its own class — with no dependency beyond
/// <see cref="DocumentStore"/> — so the coordination between <see cref="AnalysisGate"/> and a
/// document being CLOSED or REPLACED by a second <c>didOpen</c> mid-analysis can be exercised
/// directly, without the rest of that handler's dependency graph (the diagnostics publisher,
/// linter, database, and so on) standing in the way of testing it.
/// </summary>
internal sealed class SingleFlightAnalysis
{
    private readonly DocumentStore _documents;

    /// <summary>
    /// One analysis in flight per document, at most. The 250 ms debounce only guards the WAIT
    /// between an edit and analysis STARTING; once analysis has started, nothing stopped a second
    /// one starting 250 ms later on a file slow enough to still be running, each carrying its own
    /// token arrays and AST. Keyed by normalized path rather than kept on <see cref="OpenDocument"/>
    /// itself: this is scheduling state private to how analysis is driven, not a fact about the
    /// document.
    /// </summary>
    private readonly ConcurrentDictionary<string, AnalysisGate> _gates = new(StringComparer.Ordinal);

    public SingleFlightAnalysis(DocumentStore documents)
    {
        _documents = documents;
    }

    /// <summary>Drops a closed document's gate. Never required for correctness — a gate with
    /// nothing left to run just sits idle — only for not leaking one entry per path ever opened
    /// in a long session.</summary>
    public void Forget(string normalizedPath)
    {
        _gates.TryRemove(normalizedPath, out _);
    }

    public void Run(OpenDocument document, CancellationToken cancellationToken, Action<OpenDocument, CancellationToken> analyze)
    {
        AnalysisGate gate = _gates.GetOrAdd(document.Path, static _ => new AnalysisGate());
        if ( !gate.TryStart(cancellationToken) )
        {
            return;
        }

        // Reassigned per pass: a queued rerun runs under the token of the edit that asked for it,
        // not under this caller's. They are rarely the same one — the usual way a rerun gets
        // queued is an edit arriving mid-analysis, and that edit CANCELS the running pass on its
        // way past. Reusing the running pass's token abandoned every rerun before it began.
        CancellationToken token = cancellationToken;
        bool released = false;

        // The document actually analysed each pass. Reassigned after a granted rerun (below) —
        // NOT the same as leaving `document` itself pointing at whatever this call started on. A
        // second didOpen for this path replaces the store's entry with a new OpenDocument and
        // cancels this call's document, and TryStart on that second call finds the gate already
        // running and QUEUES a rerun rather than starting its own loop — the "coalesce with
        // whatever is running" the gate exists for. This loop is the one thing left to run it.
        OpenDocument current = document;

        try
        {
            // A `while (IsStillLive) { analyze; TryContinue; }` shape has a gap at the very start:
            // TryStart above already committed this call to running the gate's loop, but between
            // that commit and the loop's first liveness check, a REPLACING didOpen can land, call
            // TryStart itself, find the gate running, and queue a rerun — then this call's first
            // check finds `current` already gone and would exit through `finally` before ever
            // calling TryContinue, dropping the very rerun the replacement just queued. Looping
            // unconditionally and checking liveness INSIDE, ahead of `analyze` rather than as the
            // loop's own condition, means TryContinue is always reached — whether the document
            // this pass started with lived long enough to be analysed at all or not — so a rerun
            // queued in that opening gap is not lost the same way one queued after a normal pass
            // no longer is (see the comment on the refetch below).
            while ( true )
            {
                if ( AnalysisGate.IsStillLive(_documents, current) )
                {
                    try
                    {
                        analyze(current, token);
                    }
                    catch ( OperationCanceledException )
                    {
                        // A newer edit — or a second didOpen replacing the document outright —
                        // arrived and this pass was abandoned partway. Not a failure, and nothing
                        // to publish: the run that superseded it publishes for the text that
                        // replaced this one's.
                        //
                        // Swallowed rather than returned on, which is the whole bug this shape
                        // fixes. The edit that cancelled this pass is also the one that QUEUED the
                        // rerun, so returning here skipped the only check that would have run it —
                        // and since cancellation is the common way a rerun gets queued, the rerun
                        // path was dead in its ordinary case. The file then kept diagnostics for
                        // text the user had already replaced until something else was typed.
                    }
                    catch ( Exception exception )
                    {
                        Log.Error(exception, "Analysis failed for {Path}", current.Path);
                    }
                }

                // Releasing the gate and taking a queued rerun are ONE step. As two, a request
                // arriving between them set a flag with nobody left to read it. Reached even when
                // the branch above was skipped entirely — see the loop comment.
                if ( !gate.TryContinue(out token) )
                {
                    released = true;
                    return;
                }

                // A rerun was granted — re-read the store's CURRENT document for this path before
                // acting on it. Left as `current` from before this grant, a rerun queued by a
                // REPLACING didOpen (rather than an edit to the same document) found that document
                // already gone, was skipped by the liveness check above without ever calling
                // `analyze` for it, and the loop would have gone on skipping it forever — silently
                // dropping the new document's first analysis, which is the bug: a file opened
                // during startup indexing (slow enough for two didOpens to land before the first
                // analysis finishes) sometimes never got analysed at all, with no symptom beyond an
                // outline and diagnostics that stayed empty until an edit. TryGet failing here
                // means the path closed in the meantime; `current` is left as-is and the next
                // liveness check skips it, same as any other closed document.
                if ( _documents.TryGet(document.Path, out OpenDocument refreshed) )
                {
                    current = refreshed;
                }
            }
        }
        finally
        {
            if ( !released )
            {
                gate.Release();
            }
        }
    }
}
