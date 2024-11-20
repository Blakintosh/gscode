using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using CommandLine;

namespace GSCode.NET;

// The command line options and associated LSP communication channels code was adapted from Microsoft's Azure Bicep language server, which is licensed under the MIT License.
// https://github.com/Azure/bicep/blob/main/src/Bicep.LangServer/Program.cs
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Additionally, the code was edited with the help of AI.

public class ServerOptions
{
	[Option("stdio", Required = false, HelpText = "Use stdio as the communication channel.")]
	public bool Stdio { get; set; } = default!;

	[Option("pipe", Required = false, HelpText = "Use named pipes (or socket files) as the communication channel.")]
	public string? Pipe { get; set; } = default!;
	
	[Option("socket", Required = false, HelpText = "Uses a socket as the communication channel. Provide the port number.")]
	public int? Socket { get; set; } = default!;
}

public static class StreamResolver
{
    public static async Task<(Stream input, Stream output, IDisposable? disposable)> ResolveAsync(ServerOptions options, CancellationToken cancellationToken)
    {
        // Use a pipe
        if (options.Pipe is { } pipeName)
        {
            return await ResolvePipeAsync(pipeName, cancellationToken);
        }
        // Use a socket
        if (options.Socket is { } port) 
        {
            return await ResolveSocketAsync(port, cancellationToken);
        }
        
        // Use stdio
        return (Console.OpenStandardInput(), Console.OpenStandardOutput(), null);
    }

    private static async Task<(Stream input, Stream output, IDisposable disposable)> ResolvePipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        if (pipeName.StartsWith(@"\\.\pipe\"))
        {
            // VSCode on Windows prefixes the pipe with \\.\pipe\
            pipeName = pipeName[@"\\.\pipe\".Length..];
        }

        NamedPipeClientStream clientPipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(cancellationToken);
        
        return (clientPipe, clientPipe, clientPipe);
    }

    private static async Task<(Stream input, Stream output, IDisposable disposable)> ResolveSocketAsync(int port, CancellationToken cancellationToken)
    {
        TcpClient tcpClient = new();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        NetworkStream tcpStream = tcpClient.GetStream();

        return (tcpStream, tcpStream, tcpClient);
    }
}