using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace GSCode.Server.Transport;

/// <summary>
/// Opens the input/output streams for the selected transport. Pipe is the primary
/// (VSCode default); stdio is the fallback when no option is given.
/// </summary>
public static class TransportResolver
{
    /// <summary>
    /// How long to wait for the other end before giving up.
    ///
    /// The client creates its pipe or socket and then spawns us, so a connection that is going to
    /// happen happens immediately; a wait that reaches this bound means the other end is gone.
    /// Without it, <see cref="NamedPipeClientStream.ConnectAsync(CancellationToken)"/> waits
    /// forever and a client that died between spawn and listen leaves an orphaned server process
    /// with nothing to end it.
    /// </summary>
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Result of transport resolution; the owner (if any) must be disposed on shutdown.</summary>
    /// <param name="Description">
    /// What was connected, for the startup log. Which transport is in use is the first thing a
    /// "the extension says the server never started" report needs, and nothing said it.
    /// </param>
    public sealed record ResolvedTransport(Stream Input, Stream Output, IDisposable? Owner, string Description);

    /// <summary>
    /// Connects the transport described by <paramref name="options"/> and returns its streams.
    /// </summary>
    /// <exception cref="ArgumentException">More than one transport was named, or one was named emptily.</exception>
    /// <exception cref="TimeoutException">The other end did not accept the connection.</exception>
    public static async Task<ResolvedTransport> ResolveAsync(TransportOptions options, CancellationToken cancellationToken)
    {
        // Counted rather than tested in precedence order. The old shape took the first option it
        // recognised and ignored the rest in silence, so `--stdio --pipe foo` used the pipe and
        // `--pipe a --socket 1` used the pipe — a server listening somewhere the caller did not ask
        // for, which presents as the client waiting forever with no error anywhere.
        int named = 0;
        if ( options.PipeName is not null )
        {
            named++;
        }

        if ( options.SocketPort is not null )
        {
            named++;
        }

        if ( options.Stdio )
        {
            named++;
        }

        if ( named > 1 )
        {
            throw new ArgumentException("Name one transport: --pipe, --socket or --stdio.", nameof(options));
        }

        if ( options.PipeName is not null )
        {
            // Whitespace-aware, unlike the null check this replaced: `--pipe ""` reached
            // NamedPipeClientStream and came back out as an ArgumentException from inside the BCL.
            if ( string.IsNullOrWhiteSpace(options.PipeName) )
            {
                throw new ArgumentException("--pipe was given without a pipe name.", nameof(options));
            }

            return await ConnectPipeAsync(options.PipeName, cancellationToken);
        }

        if ( options.SocketPort is not null )
        {
            return await ConnectSocketAsync(options.SocketPort.Value, cancellationToken);
        }

        // Both the explicit --stdio and the no-options default. The flag used to be declared and
        // never read, so it worked only by falling through to here.
        return new ResolvedTransport(
            Console.OpenStandardInput(), Console.OpenStandardOutput(), Owner: null, Description: "stdio");
    }

    private static async Task<ResolvedTransport> ConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        // VSCode on Windows passes the fully-qualified pipe path; NamedPipeClientStream wants the bare name.
        const string windowsPipePrefix = @"\.\pipe\";
        string bareName = pipeName.Trim();
        if ( bareName.StartsWith(windowsPipePrefix, StringComparison.Ordinal) )
        {
            bareName = bareName[windowsPipePrefix.Length..];
        }

        NamedPipeClientStream pipe = new(".", bareName, PipeDirection.InOut, PipeOptions.Asynchronous);

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(s_connectTimeout);

        try
        {
            await pipe.ConnectAsync(attempt.Token);
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            await pipe.DisposeAsync();
            throw new TimeoutException(
                $"Timed out after {s_connectTimeout.TotalSeconds:F0}s connecting to named pipe '{bareName}'.");
        }

        return new ResolvedTransport(pipe, pipe, pipe, $"pipe {bareName}");
    }

    private static async Task<ResolvedTransport> ConnectSocketAsync(int port, CancellationToken cancellationToken)
    {
        TcpClient client = new();

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(s_connectTimeout);

        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, attempt.Token);
        }
        catch ( OperationCanceledException ) when ( !cancellationToken.IsCancellationRequested )
        {
            client.Dispose();
            throw new TimeoutException(
                $"Timed out after {s_connectTimeout.TotalSeconds:F0}s connecting to 127.0.0.1:{port}.");
        }

        NetworkStream stream = client.GetStream();
        return new ResolvedTransport(stream, stream, client, $"socket 127.0.0.1:{port}");
    }
}
