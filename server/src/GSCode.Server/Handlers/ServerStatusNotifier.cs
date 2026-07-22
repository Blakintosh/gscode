using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Keeps the status-bar tooltip's memory figure current.
///
/// It was previously set once, from the <c>gscode/indexingComplete</c> payload, and then never
/// again — so it showed whatever the server happened to be holding the instant indexing finished,
/// which is both the least interesting moment to sample and the one guaranteed to be stale a
/// minute later.
///
/// Sampling is cheap; SENDING is what costs, so a notification only goes out when the number has
/// actually moved. An idle server settles and then produces no traffic at all, while a server
/// churning through edits keeps the tooltip honest.
///
/// This is deliberately separate from the memory LOGGING behind GSCODE_INSTRUMENTATION. That
/// writes a multi-line report into the log every couple of seconds and is a debugging tool; this
/// is one small number for a tooltip, and is always on.
/// </summary>
public sealed class ServerStatusNotifier
{
    /// <summary>How often to sample. Slow enough to be free, fast enough to feel live.</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Movement worth telling the client about. Below this the number would not change the
    /// rounded megabytes the tooltip prints, so the notification would say nothing.
    /// </summary>
    private const long ReportThresholdBytes = 1024 * 1024;

    private readonly ILanguageServerFacade _server;

    public ServerStatusNotifier(ILanguageServerFacade server)
    {
        _server = server;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        long lastSentBytes = long.MinValue;

        try
        {
            while ( !cancellationToken.IsCancellationRequested )
            {
                long workingSetBytes = Environment.WorkingSet;

                if ( Math.Abs(workingSetBytes - lastSentBytes) >= ReportThresholdBytes )
                {
                    lastSentBytes = workingSetBytes;
                    _server.SendNotification(
                        "gscode/serverStatus",
                        new ServerStatusParams(workingSetBytes / (1024.0 * 1024.0)));
                }

                await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch ( OperationCanceledException )
        {
            // Shutting down — not an error.
        }
        catch ( Exception exception )
        {
            // A dead status line must never take the server with it.
            Log.Error(exception, "Server status notifier stopped unexpectedly");
        }
    }
}
