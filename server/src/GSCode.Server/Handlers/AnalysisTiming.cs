namespace GSCode.Server.Handlers;

/// <summary>
/// The one clock the interactive analysis path is measured against.
///
/// It is a constant in one place because two things now read it and they must not drift apart:
/// <see cref="TextSyncHandler"/>, which waits this long after a keystroke before analysing and
/// warns when one analysis takes at or past it, and the corpus budget gate, which asserts that no
/// single lint rule comes near a share of it. A budget written against a hardcoded 250 would go on
/// passing if the debounce were ever shortened, which is exactly when it would need to fail.
///
/// <see cref="DependentDiagnosticsRefresher"/>'s longer fan-out debounce is deliberately NOT here:
/// it coalesces cross-file refreshes rather than one document's own re-analysis, so it is a
/// different quantity that happens to be measured in the same unit.
/// </summary>
public static class AnalysisTiming
{
    /// <summary>
    /// The longest a document's re-analysis waits after the last edit, and the budget everything the
    /// editor runs on a keystroke — the parse and every cross-file lint — has to fit inside to keep
    /// one analysis from overlapping the next. A file that analyses quickly waits less: see
    /// <see cref="DebounceFor"/>.
    /// </summary>
    public const int DebounceMilliseconds = 250;

    /// <summary>
    /// The shortest wait. Long enough that the pause between two keys of a word usually falls
    /// inside it, so a half-typed name is not reported as unknown on its way to being typed.
    /// </summary>
    public const int MinimumDebounceMilliseconds = 125;

    /// <summary>
    /// How long to wait after an edit to a document whose last analysis took
    /// <paramref name="lastAnalysisMilliseconds"/>: three times that, between
    /// <see cref="MinimumDebounceMilliseconds"/> and <see cref="DebounceMilliseconds"/>, or the
    /// longest when nothing has been measured yet.
    ///
    /// A fixed 250 ms was most of what an edit's diagnostics took to appear. Over stdio at 50,000
    /// heavy BO3 files, _globallogic.gsc analysed in 30–33 ms and its diagnostics landed 280–307 ms
    /// after the last change. Three times the analysis keeps the server's share of a burst of typing
    /// to about a third, and a slow file still waits the whole budget.
    /// </summary>
    public static int DebounceFor(double lastAnalysisMilliseconds)
    {
        if ( lastAnalysisMilliseconds <= 0 )
        {
            return DebounceMilliseconds;
        }

        return (int)Math.Clamp(lastAnalysisMilliseconds * 3, MinimumDebounceMilliseconds, DebounceMilliseconds);
    }
}
