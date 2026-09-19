using Serilog;

namespace GSCode.Server.Configuration;

/// <summary>
/// The shared "has the pipe had a moment to settle" clock.
///
/// A notification sent inside the initialize/initialized window is silently dropped by the
/// transport, and <c>OnStarted</c> itself does not reliably run late enough on its own to avoid
/// it. Every notification the server sends UNPROMPTED this early —
/// <c>gscode/serverReady</c>, the indexing progress family, and <c>gscode/gameMismatch</c> from a
/// restored tab's own <c>didOpen</c> — therefore needs the same protection. This used to be
/// private to indexing (<see cref="GSCode.Server.Handlers.IndexProgressNotifier"/> started its
/// own <c>Task.Delay(500)</c>), which is exactly why <c>gscode/serverReady</c> went out
/// unprotected: nothing else knew the gate existed.
///
/// Lazily started on first use rather than at construction — the clock should start counting from
/// when the connection could plausibly start working, which nothing before the first send is
/// asked for guarantees, and a container is built well before that.
/// </summary>
public sealed class ConnectionSettleGate
{
    private readonly Lock _gate = new();
    private Task? _settled;

    /// <summary>
    /// The shared clock, started on first read. Exposed for a caller that has its own reason to
    /// defer a whole SEQUENCE of sends on it (<see cref="GSCode.Server.Handlers.IndexProgressNotifier.SendNothingBefore"/>)
    /// rather than one-off sends via <see cref="SendOnceSettled"/>.
    /// </summary>
    public Task Settled
    {
        get
        {
            lock ( _gate )
            {
                _settled ??= Task.Delay(500, CancellationToken.None);
                return _settled;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="send"/> once the clock has settled — immediately, if it already has.
    /// A continuation rather than an awaited <see cref="Task"/>, so a synchronous caller (an LSP
    /// handler returning <c>Task.CompletedTask</c>, not actually asynchronous) can use this
    /// without becoming async itself, and without the async-over-sync ceremony that would need.
    /// </summary>
    public void SendOnceSettled(Action send)
    {
        RunOnceSettled(Settled, send);
    }

    /// <summary>
    /// The same deferral against a clock the caller already holds, for
    /// <see cref="GSCode.Server.Handlers.IndexProgressNotifier"/>, which is handed the task rather
    /// than the gate. Shared so the two cannot drift on either of the properties below.
    /// </summary>
    public static void RunOnceSettled(Task settled, Action send)
    {
        if ( settled.IsCompleted )
        {
            Send(send);
            return;
        }

        // NOT ExecuteSynchronously, which is what this used to be. Task.Delay completes on a timer
        // thread, so running the send inline there serialised a notification and wrote it to the
        // pipe ahead of every other timer in the process.
        _ = settled.ContinueWith(
            _ => Send(send),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Sends, and keeps a failure to itself.
    ///
    /// The deferred call has no caller left to throw to: the continuation task is discarded, so an
    /// exception out of it became an unobserved task exception that nothing reported. A lost
    /// notification is worth a line in the log and nothing more.
    /// </summary>
    private static void Send(Action send)
    {
        try
        {
            send();
        }
        catch ( Exception exception )
        {
            Log.Error(exception, "A deferred notification could not be sent");
        }
    }
}
