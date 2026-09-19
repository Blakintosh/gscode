using GSCode.Server.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// A settings push is all-or-nothing.
///
/// Apply used to assign twenty-five properties one after another, on the configuration thread,
/// while every handler thread read them — so a reader could observe a new Game beside an old
/// RawPath, a combination nobody ever sent. The same shape meant a payload that threw part-way
/// through left the settings half-applied, and since Apply runs inside OnInitialize, a malformed
/// value failed the whole handshake it arrived on.
/// </summary>
public class SettingsSnapshotTests
{
    private static JToken Payload(string body)
    {
        return JToken.Parse("{ \"gscode\": " + body + " }");
    }

    [Fact]
    public void AGoodPayloadIsApplied()
    {
        ServerSettings settings = new();

        settings.Apply(Payload("{ \"game\": \"cod4\", \"codeLens\": { \"enabled\": true } }"));

        Assert.Equal("cod4", settings.Game);
        Assert.True(settings.CodeLensEnabled);
    }

    [Fact]
    public void AMalformedValueLeavesEveryOtherSettingAlone()
    {
        // The int conversion is what throws. Before, "game" had already been assigned by the time
        // it did, so the server ran as cod4 with everything after that key still at its old value.
        ServerSettings settings = new();

        settings.Apply(Payload("{ \"game\": \"cod4\", \"format\": { \"maxBlankLines\": \"lots\" } }"));

        Assert.Equal("bo3", settings.Game);
        Assert.Equal(2, settings.FormatMaxBlankLines);
    }

    [Fact]
    public void AMalformedPayloadDoesNotThrowOutOfApply()
    {
        // Apply runs inside OnInitialize. A throw there fails the initialize handshake, so the
        // server never starts at all over one bad key.
        ServerSettings settings = new();

        settings.Apply(Payload("{ \"enableWorkspaceCache\": { \"not\": \"a bool\" } }"));

        Assert.True(settings.EnableWorkspaceCache);
    }

    [Fact]
    public void AMissingKeyKeepsWhatWasThereBefore()
    {
        ServerSettings settings = new();
        settings.Apply(Payload("{ \"game\": \"waw\" }"));

        settings.Apply(Payload("{ \"codeLens\": { \"enabled\": true } }"));

        Assert.Equal("waw", settings.Game);
        Assert.True(settings.CodeLensEnabled);
    }

    [Fact]
    public void ANegativeBlankLineLimitIsClamped()
    {
        // The formatter emits this many blank lines; a negative one is a knob nobody meant to have.
        ServerSettings settings = new();

        settings.Apply(Payload("{ \"format\": { \"maxBlankLines\": -3 } }"));

        Assert.Equal(0, settings.FormatMaxBlankLines);
    }

    [Fact]
    public void ASetterStillWorksForTheTestsThatUseOne()
    {
        // The public surface is unchanged: the snapshot is an implementation detail, not a new API
        // every handler and test had to be rewritten against.
        ServerSettings settings = new() { CodeLensEnabled = true };

        Assert.True(settings.CodeLensEnabled);

        settings.DiagnosticsScope = "open";

        Assert.Equal("open", settings.DiagnosticsScope);
    }
}
