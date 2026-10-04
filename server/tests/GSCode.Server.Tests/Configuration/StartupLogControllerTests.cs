using GSCode.Server.Logging;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// The state <c>StartupLogController</c> holds so <c>OnInitialize</c> (which learns the requested
/// level) and the startup task's <c>finally</c> block (which settles it, in different files after
/// the <c>Program.cs</c> split) can agree on the level without a shared local variable to close over.
///
/// <see cref="ServerLogLevel.StartupFloor"/> already covers the FLOOR rule itself; these tests are
/// about the controller applying it at the right time and remembering the right thing afterwards.
/// </summary>
public class StartupLogControllerTests
{
    [Fact]
    public void SettingARequestedLevelAppliesTheFloorImmediately()
    {
        LoggingLevelSwitch levelSwitch = new(LogEventLevel.Verbose);
        StartupLogController controller = new(levelSwitch);

        controller.SetRequested(LogEventLevel.Warning);

        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void SettlingDropsToWhatWasRequested()
    {
        LoggingLevelSwitch levelSwitch = new(LogEventLevel.Verbose);
        StartupLogController controller = new(levelSwitch);

        controller.SetRequested(LogEventLevel.Warning);
        controller.Settle();

        Assert.Equal(LogEventLevel.Warning, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void OffIsAppliedImmediatelyRatherThanFlooredToInformation()
    {
        // Off is the one setting that asks for silence rather than for less detail, and
        // ServerLogLevel.StartupFloor honours it outright — nothing to verify here beyond the
        // controller actually calling through to that rule rather than a floor of its own.
        LoggingLevelSwitch levelSwitch = new(LogEventLevel.Verbose);
        StartupLogController controller = new(levelSwitch);

        LogEventLevel off = ServerLogLevel.FromSetting("off");
        controller.SetRequested(off);

        Assert.Equal(off, levelSwitch.MinimumLevel);

        controller.Settle();

        Assert.Equal(off, levelSwitch.MinimumLevel);
    }

    [Fact]
    public void SettlingBeforeAnythingWasRequestedUsesTheDefault()
    {
        // A host that sends no initializationOptions at all never calls SetRequested; Settle must
        // still land on something sane rather than leaving the switch at whatever it started at.
        LoggingLevelSwitch levelSwitch = new(LogEventLevel.Verbose);
        StartupLogController controller = new(levelSwitch);

        controller.Settle();

        Assert.Equal(LogEventLevel.Information, levelSwitch.MinimumLevel);
    }
}
