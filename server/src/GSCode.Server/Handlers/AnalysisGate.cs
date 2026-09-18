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
    private int _running;
    private int _rerunRequested;

    /// <summary>
    /// True when the caller should run now; false when another run is already in flight and has
    /// been told to loop once more instead.
    /// </summary>
    public bool TryStart()
    {
        if ( Interlocked.Exchange(ref _running, 1) == 0 )
        {
            return true;
        }

        Volatile.Write(ref _rerunRequested, 1);
        return false;
    }

    /// <summary>Clears any rerun request and reports whether one had been made.</summary>
    public bool ConsumeRerunRequest()
    {
        return Interlocked.Exchange(ref _rerunRequested, 0) != 0;
    }

    public void Finish()
    {
        Volatile.Write(ref _running, 0);
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
