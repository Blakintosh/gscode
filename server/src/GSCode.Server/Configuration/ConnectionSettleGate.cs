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
        Task settled = Settled;
        if ( settled.IsCompleted )
        {
            send();
            return;
        }

        _ = settled.ContinueWith(
            _ => send(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
