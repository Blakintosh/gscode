namespace GSCode.Server.Configuration;

/// <summary>
/// Owns the startup indexing task's cancellation and lets shutdown wait for it to actually stop.
///
/// Without it the task runs detached: nothing can tell it to stop and nothing waits for it before
/// <c>cacheHolder.CloseAsync()</c> runs on exit, so a server closed during a cold index races its own
/// cache close — in-flight <c>SqliteCache.Enqueue</c> calls land on a completing write channel, count
/// as dropped, and leave the next start's cache partial and its report wrong.
///
/// One instance covers the whole startup task — the index itself, the settle delay in front of
/// its notifications, the cache drain after it, and the status notifier that follows — so a single
/// cancellation reaches all of it rather than stopping the index while its neighbours run on
/// regardless.
/// </summary>
public sealed class IndexingLifetime
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _task;

    /// <summary>
    /// The token every piece of the startup task should observe. Fetched once, before the task is
    /// created, since the task itself needs to close over it.
    /// </summary>
    public CancellationToken Token
    {
        get { return _cancellation.Token; }
    }

    /// <summary>
    /// Records the running startup task, so <see cref="CancelAndWaitAsync"/> has something to
    /// wait for. Set once the task exists — the task's own body already closed over
    /// <see cref="Token"/> before this is called, so there is no gap where cancelling would have
    /// nothing to cancel.
    /// </summary>
    public void SetTask(Task task)
    {
        lock ( _gate )
        {
            _task = task;
        }
    }

    /// <summary>
    /// Cancels the startup task and waits for it to actually finish, bounded by
    /// <paramref name="timeout"/> so a task that ignores cancellation (or was never started)
    /// cannot hang shutdown indefinitely. Safe to call with nothing running: a null task is simply
    /// nothing to wait for.
    /// </summary>
    public async Task CancelAndWaitAsync(TimeSpan timeout)
    {
        await _cancellation.CancelAsync().ConfigureAwait(false);

        Task? task;
        lock ( _gate )
        {
            task = _task;
        }

        if ( task is null )
        {
            return;
        }

        await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
    }
}
