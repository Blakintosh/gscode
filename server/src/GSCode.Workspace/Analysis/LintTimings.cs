using System.Diagnostics;

namespace GSCode.Workspace.Analysis;

/// <summary>
/// Per-rule timings for ONE run of the lint pass, collected by a caller that asks for them.
///
/// <see cref="GSCode.Core.Instrumentation.PerfTracker"/> already names every rule's scope, but
/// every one of its methods is <c>[Conditional("GSCODE_INSTRUMENTATION")]</c> — so a build without
/// that flag records nothing, and anything ASSERTING on a rule's cost would pass by measuring
/// nothing at all. That is the failure this exists to avoid: the corpus budget gate runs in an
/// ordinary Release build, so it needs a timing path that is really there in one.
///
/// One instance per file. Not thread-safe and not meant to be: a pass is sequential, and the
/// caller that owns the instance is the one timing that file.
/// </summary>
public sealed class LintTimings
{
    private readonly Dictionary<string, double> _milliseconds = new(StringComparer.Ordinal);

    /// <summary>
    /// Milliseconds per scope name, for the run since the last <see cref="Clear"/>. Scopes that did
    /// not run are ABSENT rather than zero — several rules stand down on a dialect or on an
    /// unfinished index, and a zero would read as "ran, cost nothing".
    /// </summary>
    public IReadOnlyDictionary<string, double> Milliseconds
    {
        get { return _milliseconds; }
    }

    /// <summary>Drops everything recorded, so one instance can time file after file.</summary>
    public void Clear()
    {
        _milliseconds.Clear();
    }

    /// <summary>
    /// Adds to a scope rather than replacing it, so a scope entered twice in one pass reports the
    /// sum. Nothing does that today; accumulating means a future one is not silently halved.
    /// </summary>
    internal void Record(string scopeName, long elapsedTicks)
    {
        double elapsedMilliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;

        _milliseconds.TryGetValue(scopeName, out double running);
        _milliseconds[scopeName] = running + elapsedMilliseconds;
    }
}
