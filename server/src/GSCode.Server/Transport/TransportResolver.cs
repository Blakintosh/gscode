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
    /// <summary>Result of transport resolution; the owner (if any) must be disposed on shutdown.</summary>
    public sealed record ResolvedTransport(Stream Input, Stream Output, IDisposable? Owner);

    /// <summary>
    /// Connects the transport described by <paramref name="options"/> and returns its streams.
    /// </summary>
    public static async Task<ResolvedTransport> ResolveAsync(TransportOptions options, CancellationToken cancellationToken)
    {
        if ( options.PipeName is not null )
        {
            return await ConnectPipeAsync(options.PipeName, cancellationToken);
        }

        if ( options.SocketPort is not null )
        {
            return await ConnectSocketAsync(options.SocketPort.Value, cancellationToken);
        }

        return new ResolvedTransport(Console.OpenStandardInput(), Console.OpenStandardOutput(), Owner: null);
    }

    private static async Task<ResolvedTransport> ConnectPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        // VSCode on Windows passes the fully-qualified pipe path; NamedPipeClientStream wants the bare name.
        const string windowsPipePrefix = @"\\.\pipe\";
        if ( pipeName.StartsWith(windowsPipePrefix, StringComparison.Ordinal) )
        {
            pipeName = pipeName[windowsPipePrefix.Length..];
        }

        NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancellationToken);

        return new ResolvedTransport(pipe, pipe, pipe);
    }

    private static async Task<ResolvedTransport> ConnectSocketAsync(int port, CancellationToken cancellationToken)
    {
        TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);

        NetworkStream stream = client.GetStream();
        return new ResolvedTransport(stream, stream, client);
    }
}
