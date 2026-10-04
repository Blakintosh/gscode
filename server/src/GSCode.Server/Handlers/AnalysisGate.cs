using GSCode.Workspace.Documents;

namespace GSCode.Server.Handlers;

/// <summary>
/// Coalesces concurrent analysis requests for one document into: whichever is already running,
/// plus at most one more queued to run immediately after against whatever text is current by then.
///
/// The 250 ms debounce only guards the WAIT between an edit and analysis STARTING; once one has
/// started, nothing stopped a second beginning 250 ms later on a file slow enough to still be
/// running, each carrying its own token arrays and tree.
///
/// <c>OpenDocument.Publish</c>'s version CAS decides which analysis's RESULT wins when two overlap
/// anyway (a request thread's <see cref="DocumentStore.AnalyzeIfStale"/> can still run alongside
/// this), so the gate is about not PAYING for more analyses in flight than the debounce intended,
/// not about the correctness of what gets published.
/// </summary>
internal sealed class AnalysisGate
{
    /// <summary>
    /// A lock rather than two interlocked flags, because RELEASING the gate and checking for a
    /// queued rerun have to be one step. As two they leave a window: a request arriving after the
    /// check and before the release sets a flag nobody is left to read, and the keystroke that
    /// asked for it is never analysed.
    ///
    /// The token has to live in here for the same reason it cannot be an interlocked word. It is
    /// the queueing request's own token, not the running pass's — the pass in flight is normally
    /// CANCELLED by the very edit that queues the rerun, so reusing its token would abandon the
    /// rerun before it started.
    /// </summary>
    private readonly Lock _gate = new();

    private bool _running;
    private bool _rerunRequested;
    private CancellationToken _rerunToken;

    /// <summary>
    /// True when the caller should run now; false when another run is already in flight and this
    /// request has been queued behind it instead.
    /// </summary>
    /// <param name="cancellationToken">
    /// The caller's token, kept when the request is queued so the rerun runs under the token of the
    /// edit that asked for it.
    /// </param>
    public bool TryStart(CancellationToken cancellationToken)
    {
        lock ( _gate )
        {
            if ( !_running )
            {
                _running = true;
                return true;
            }

            // The newest request wins the slot. An older queued token belongs to an edit that has
            // since been superseded, and its source was cancelled by the edit replacing it here.
            _rerunRequested = true;
            _rerunToken = cancellationToken;
            return false;
        }
    }

    /// <summary>
    /// Either hands the running caller a queued rerun to perform (keeping the gate held), or
    /// releases the gate. One operation, because the two cannot be separated without dropping a
    /// request that arrives between them.
    /// </summary>
    public bool TryContinue(out CancellationToken rerunToken)
    {
        lock ( _gate )
        {
            if ( _rerunRequested )
            {
                _rerunRequested = false;
                rerunToken = _rerunToken;
                _rerunToken = CancellationToken.None;
                return true;
            }

            _running = false;
            rerunToken = CancellationToken.None;
            return false;
        }
    }

    /// <summary>
    /// Releases the gate and abandons anything queued behind it.
    ///
    /// For the paths that stop without asking for more work: the document is no longer the one the
    /// store holds, or something unexpected escaped the run. A rerun dropped here is a rerun for a
    /// document nothing can reach, or one whose caller has already failed — either way, leaving the
    /// gate held would stop that path analysing for the rest of the session, which is worse than
    /// losing one pass.
    /// </summary>
    public void Release()
    {
        lock ( _gate )
        {
            _running = false;
            _rerunRequested = false;
            _rerunToken = CancellationToken.None;
        }
    }

    /// <summary>
    /// Whether the document a run captured is still the one the store holds.
    ///
    /// An analysis holds a direct reference to the <see cref="OpenDocument"/> it started on. If
    /// that document is closed, or a second didOpen replaces it, the run has no way to notice: it
    /// keeps going and publishes and commits for an object nothing else can reach — able to
    /// overwrite the live document's fresher answer purely by finishing after it. Reference
    /// equality rather than the path, because the orphan and its replacement share one.
    /// </summary>
    public static bool IsStillLive(DocumentStore documents, OpenDocument document)
    {
        return documents.TryGet(document.Path, out OpenDocument current) && ReferenceEquals(current, document);
    }
}
