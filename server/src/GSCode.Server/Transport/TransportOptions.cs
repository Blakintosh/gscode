using CommandLine;

namespace GSCode.Server.Transport;

/// <summary>
/// Command-line transport selection. The VSCode client launches us with --pipe by default;
/// --stdio and --socket exist for other hosts and debugging.
/// </summary>
public sealed class TransportOptions
{
    [Option("stdio", Required = false, HelpText = "Communicate over standard input/output.")]
    public bool Stdio { get; set; }

    [Option("pipe", Required = false, HelpText = "Communicate over the given named pipe (or socket file).")]
    public string? PipeName { get; set; }

    [Option("socket", Required = false, HelpText = "Communicate over a TCP socket on the given localhost port.")]
    public int? SocketPort { get; set; }
}
