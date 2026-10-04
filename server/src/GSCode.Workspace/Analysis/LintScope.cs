using System.Diagnostics;
using GSCode.Core.Instrumentation;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// One rule's timing scope in the lint pass — the instrumented build's <see cref="PerfTracker"/>
/// scope and an ordinary build's optional <see cref="LintTimings"/> sink, entered once.
///
/// It exists so each rule in <see cref="WorkspaceLints"/> is wrapped ONCE. The two mechanisms
/// answer different questions — PerfTracker aggregates across a whole run for the perf report,
/// LintTimings hands one file's numbers to the caller that asked — and writing both at every call
/// site would be two things to keep in step around twenty-odd rules.
///
/// A struct, so a pass with no sink allocates nothing and pays a null check per rule. When the sink
/// IS null the timestamp is never read either: the cost of the measurement should not be carried by
/// the path that is not measuring.
/// </summary>
public readonly struct LintScope : IDisposable
{
    private readonly LintTimings? _timings;
    private readonly string _scopeName;
    private readonly long _startTimestamp;

    private LintScope(string scopeName, LintTimings? timings, long startTimestamp)
    {
        _scopeName = scopeName;
        _timings = timings;
        _startTimestamp = startTimestamp;
    }

    /// <summary>Opens the scope. Pair it with a <c>using</c> so a throwing rule still closes it.</summary>
    public static LintScope For(string scopeName, LintTimings? timings)
    {
        PerfTracker.Begin(scopeName);

        return new LintScope(
            scopeName,
            timings,
            timings is null ? 0L : Stopwatch.GetTimestamp());
    }

    public void Dispose()
    {
        PerfTracker.End();

        if ( _timings is not null )
        {
            _timings.Record(_scopeName, Stopwatch.GetTimestamp() - _startTimestamp);
        }
    }
}
