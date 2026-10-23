using System.Runtime.ExceptionServices;

namespace GSCode.Server.Handlers;

/// <summary>
/// Background work on threads of its own at below-normal priority, one per core but one.
///
/// Not the thread pool. The lint sweep takes every core it is given, and on the pool it ran at the
/// priority every request handler runs at: at 10,000 heavy BO3 files a document-symbol request made
/// during a cold start's sweep waited 416 ms at p99 against 22 ms typically. Below normal, the
/// scheduler runs a request's thread whenever it is ready and gives this work what is left.
/// </summary>
internal static class BelowNormalWorkers
{
    /// <summary>
    /// Runs <paramref name="body"/> for every index below <paramref name="count"/>. Workers stop
    /// claiming indices on cancellation or on the first exception, which is rethrown once every
    /// worker has finished; cancellation is then reported as <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task RunAsync(string name, int count, Action<int> body, CancellationToken cancellationToken)
    {
        if ( count == 0 )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        int next = -1;
        ExceptionDispatchInfo? failure = null;
        int workers = Math.Min(count, Math.Max(1, Environment.ProcessorCount - 1));
        int running = workers;
        TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Work()
        {
            try
            {
                while ( !cancellationToken.IsCancellationRequested && Volatile.Read(ref failure) is null )
                {
                    int index = Interlocked.Increment(ref next);
                    if ( index >= count )
                    {
                        break;
                    }

                    body(index);
                }
            }
            catch ( Exception exception )
            {
                Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
            }
            finally
            {
                if ( Interlocked.Decrement(ref running) == 0 )
                {
                    finished.SetResult();
                }
            }
        }

        for ( int worker = 0; worker < workers; worker++ )
        {
            Thread thread = new(Work)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = name,
            };
            thread.Start();
        }

        await finished.Task.ConfigureAwait(false);

        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
