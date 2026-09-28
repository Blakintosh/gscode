using System.Diagnostics;

namespace GSCode.Server.Handlers;

/// <summary>
/// Which of a flood of progress reports actually go out: at most one per interval, and never one
/// older than the last that went.
///
/// Both halves are needed because the reports come from inside the indexer's
/// <c>Parallel.ForEachAsync</c>, on every worker thread at once.
///
/// The claim is a compare-exchange: only the thread that writes the new timestamp gets the slot. A
/// shared <see cref="Stopwatch"/> checked and then restarted is not atomic, so several workers would
/// read the same "long enough ago" and all send.
///
/// The ordering half: a parallel walk reports out of ORDER, so a slower worker's older count could
/// land after a faster one's and run the status-bar counter backwards — including after the final
/// report.
/// </summary>
internal sealed class ProgressThrottle
{
    private readonly long _intervalTicks;

    private long _lastSendTicks;
    private int _highestSent = -1;

    public ProgressThrottle(long intervalMilliseconds)
    {
        _intervalTicks = Stopwatch.Frequency * intervalMilliseconds / 1000;

        // Backdated by a whole interval so the FIRST report goes out immediately. Starting the
        // clock at "now" swallowed everything in the opening interval, which is the report that
        // makes the status bar appear at all.
        _lastSendTicks = Stopwatch.GetTimestamp() - _intervalTicks;
    }

    /// <summary>
    /// Whether this report should be sent, claiming the slot for it if so.
    /// </summary>
    /// <param name="isFinal">
    /// The report that says the work is done. It bypasses the interval — it is terminal, and a
    /// client that never sees it shows a progress bar for the rest of the session — but it still
    /// takes the ordering slot, so a straggler arriving afterwards cannot follow it with a lower
    /// number.
    /// </param>
    public bool ShouldSend(int count, bool isFinal)
    {
        if ( !isFinal && !TryClaimInterval() )
        {
            return false;
        }

        return TryAdvance(count, isFinal);
    }

    private bool TryClaimInterval()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastSendTicks);

        if ( now - last < _intervalTicks )
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastSendTicks, now, last) == last;
    }

    private bool TryAdvance(int count, bool isFinal)
    {
        while ( true )
        {
            int highest = Volatile.Read(ref _highestSent);

            if ( count <= highest )
            {
                // The final report goes out even when a higher count somehow preceded it, since it
                // is the one that ends the progress UI.
                return isFinal;
            }

            if ( Interlocked.CompareExchange(ref _highestSent, count, highest) == highest )
            {
                return true;
            }
        }
    }
}
