using GSCode.Server.Transport;
using Xunit;

namespace GSCode.Server.Tests.Transport;

/// <summary>
/// Which transport a command line selects, and what happens when it names one badly.
///
/// The old resolver tested the options in precedence order and took the first it recognised, so
/// `--stdio --pipe foo` used the pipe and `--pipe a --socket 1` used the pipe — a server listening
/// somewhere the caller did not ask for. Nothing was logged either way, and the symptom is a client
/// that waits forever on the transport it asked for.
/// </summary>
public class TransportSelectionTests
{
    [Fact]
    public async Task NoOptionsMeansStdio()
    {
        TransportResolver.ResolvedTransport transport =
            await TransportResolver.ResolveAsync(new TransportOptions(), CancellationToken.None);

        Assert.Equal("stdio", transport.Description);
        Assert.Null(transport.Owner);
    }

    [Fact]
    public async Task TheStdioFlagIsRead()
    {
        // It used to be declared and never read, so --stdio worked only by falling through to the
        // default branch — which meant it could not be distinguished from a conflicting request.
        TransportResolver.ResolvedTransport transport =
            await TransportResolver.ResolveAsync(new TransportOptions { Stdio = true }, CancellationToken.None);

        Assert.Equal("stdio", transport.Description);
    }

    [Fact]
    public async Task NamingTwoTransportsIsRefused()
    {
        TransportOptions both = new() { Stdio = true, PipeName = "gscode-test" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => TransportResolver.ResolveAsync(both, CancellationToken.None));
    }

    [Fact]
    public async Task NamingAPipeAndASocketIsRefused()
    {
        TransportOptions both = new() { PipeName = "gscode-test", SocketPort = 1234 };

        await Assert.ThrowsAsync<ArgumentException>(
            () => TransportResolver.ResolveAsync(both, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnEmptyPipeNameIsRefusedHereRatherThanInsideTheBcl(string pipeName)
    {
        TransportOptions empty = new() { PipeName = pipeName };

        ArgumentException thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => TransportResolver.ResolveAsync(empty, CancellationToken.None));

        Assert.Contains("--pipe", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenStopsTheConnectAttempt()
    {
        // The caller's cancellation has to be distinguishable from the connect timeout, since one
        // is shutdown and the other is a client that never listened.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        TransportOptions pipe = new() { PipeName = "gscode-a-pipe-nothing-is-listening-on" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TransportResolver.ResolveAsync(pipe, cancelled.Token));
    }
}
