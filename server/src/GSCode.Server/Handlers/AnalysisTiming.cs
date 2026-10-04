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
    /// How long a document's re-analysis waits after the last edit. Everything the editor runs on
    /// a keystroke — the parse and every cross-file lint — has to fit inside this to keep one
    /// analysis from overlapping the next.
    /// </summary>
    public const int DebounceMilliseconds = 250;
}
