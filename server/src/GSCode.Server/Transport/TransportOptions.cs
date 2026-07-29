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

    /// <summary>
    /// The game to analyse as, e.g. <c>cod4</c>. Passed on the COMMAND LINE rather than left to
    /// initializationOptions because the bundled data files are chosen by the active profile and are
    /// resolved while the container is built — before the initialize request arrives. Learning the
    /// game from the handshake is therefore too late: the data would already have loaded for the
    /// default game. The setting still applies on top; this only makes the first load correct.
    /// </summary>
    [Option("game", Required = false, HelpText = "The game to analyse as (cod4, waw, mw2, bo1, bo3).")]
    public string? Game { get; set; }
}
