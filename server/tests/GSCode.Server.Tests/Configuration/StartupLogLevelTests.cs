using GSCode.Server.Logging;
using Serilog.Events;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// What the log channel runs at while the server is starting.
///
/// The startup lines — resolved roots, effective settings, the index breakdown, how long the
/// server took to be ready — are written at Information, and the client's default setting is
/// "warning". Lowering the switch the moment initialize arrived therefore discarded every one of
/// them in the shipped configuration, which is the opposite of why they exist: a user attaching a
/// log to a bug report should not first have to be told to raise the level.
/// </summary>
public class StartupLogLevelTests
{
    [Theory]
    [InlineData("warning")]
    [InlineData("error")]
    public void AQuieterSettingStillLetsTheStartupLinesThroughWhileStartingUp(string setting)
    {
        LogEventLevel requested = ServerLogLevel.FromSetting(setting);

        Assert.Equal(LogEventLevel.Information, ServerLogLevel.StartupFloor(requested));
    }

    [Fact]
    public void OffIsHonouredImmediately()
    {
        // The one setting that asks for silence rather than for less detail.
        LogEventLevel off = ServerLogLevel.FromSetting("off");

        Assert.Equal(off, ServerLogLevel.StartupFloor(off));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("verbose")]
    public void ALevelAtOrBelowInformationIsHonouredImmediately(string setting)
    {
        // Nothing to hold back: these already let the startup lines through.
        LogEventLevel requested = ServerLogLevel.FromSetting(setting);

        Assert.Equal(requested, ServerLogLevel.StartupFloor(requested));
    }

    [Fact]
    public void TheFloorNeverRaisesTheLevelAboveWhatWasAsked()
    {
        // Verbose must not become Information: the floor is a floor, not a clamp in both
        // directions.
        Assert.Equal(LogEventLevel.Verbose, ServerLogLevel.StartupFloor(LogEventLevel.Verbose));
    }
}
