using System.Collections.Concurrent;
using System.Diagnostics;

namespace GSCode.Core.Instrumentation;

/// <summary>
/// Aggregates named timing scopes when the build defines GSCODE_INSTRUMENTATION;
/// every call compiles to nothing in normal builds.
/// </summary>
public static class PerfTracker
{
    private sealed class ScopeStats
    {
        public long TotalTicks;
        public long Count;
    }

    private static readonly ConcurrentDictionary<string, ScopeStats> s_scopes = new();

    // Per-thread stack of open scopes so Begin/End pairs nest correctly across threads.
    [ThreadStatic]
    private static Stack<OpenScope>? s_openScopes;

    private readonly record struct OpenScope(string Name, long StartTimestamp);

    /// <summary>
    /// Opens a timing scope on the current thread. Must be paired with a later <see cref="End"/>.
    /// </summary>
    [Conditional("GSCODE_INSTRUMENTATION")]
    public static void Begin(string scopeName)
    {
        s_openScopes ??= new Stack<OpenScope>();
        s_openScopes.Push(new OpenScope(scopeName, Stopwatch.GetTimestamp()));
    }

    /// <summary>
    /// Closes the innermost open scope on the current thread and records its elapsed time.
    /// An unmatched End is ignored rather than throwing.
    /// </summary>
    [Conditional("GSCODE_INSTRUMENTATION")]
    public static void End()
    {
        if ( s_openScopes is null || s_openScopes.Count == 0 )
        {
            return;
        }

        OpenScope scope = s_openScopes.Pop();
        long elapsedTicks = Stopwatch.GetTimestamp() - scope.StartTimestamp;

        ScopeStats stats = s_scopes.GetOrAdd(scope.Name, static _ => new ScopeStats());
        Interlocked.Add(ref stats.TotalTicks, elapsedTicks);
        Interlocked.Increment(ref stats.Count);
    }

    /// <summary>
    /// Copies the scopes recorded since the last <see cref="Reset"/> into <paramref name="into"/>,
    /// as milliseconds and call counts.
    ///
    /// Takes a SINK rather than returning a dictionary so it can be <c>[Conditional]</c>: the
    /// attribute is only legal on void methods, and illegal with an <c>out</c> parameter (CS0685),
    /// since a removed call would leave the caller's variable unassigned. <see cref="Report"/> is
    /// shaped the same way and for the same reason. A caller allocates its collection, calls this,
    /// and in an ordinary build gets an untouched empty one — the whole call having been compiled
    /// out rather than having run and found nothing.
    ///
    /// Reset before a unit of work and snapshot after it, and the result is that unit's own profile:
    /// it is how the corpus perf sweep attributes sub-phase time to ONE FILE, which the aggregate
    /// <see cref="Report"/> cannot do because it sums across the whole run.
    ///
    /// Only meaningful once the measured work is QUIESCED. The counters are written under
    /// <see cref="Interlocked"/> but read here without one, so a snapshot taken while other threads
    /// are still inside scopes can mix a scope's ticks with another's count.
    /// </summary>
    [Conditional("GSCODE_INSTRUMENTATION")]
    public static void Snapshot(IDictionary<string, (double Milliseconds, long Count)> into)
    {
        foreach ( KeyValuePair<string, ScopeStats> pair in s_scopes )
        {
            double milliseconds = pair.Value.TotalTicks * 1000.0 / Stopwatch.Frequency;
            into[pair.Key] = (milliseconds, pair.Value.Count);
        }
    }

    /// <summary>
    /// Writes one line per recorded scope (name, call count, total and mean milliseconds).
    /// </summary>
    [Conditional("GSCODE_INSTRUMENTATION")]
    public static void Report(Action<string> writeLine)
    {
        foreach ( KeyValuePair<string, ScopeStats> pair in s_scopes.OrderBy(static pair => pair.Key) )
        {
            double totalMilliseconds = pair.Value.TotalTicks * 1000.0 / Stopwatch.Frequency;
            double meanMilliseconds = totalMilliseconds / Math.Max(1, pair.Value.Count);
            writeLine($"{pair.Key}: {pair.Value.Count} calls, {totalMilliseconds:F1} ms total, {meanMilliseconds:F3} ms mean");
        }
    }

    /// <summary>
    /// Clears all recorded scope statistics (used between measurement runs).
    /// </summary>
    [Conditional("GSCODE_INSTRUMENTATION")]
    public static void Reset()
    {
        s_scopes.Clear();
    }
}
