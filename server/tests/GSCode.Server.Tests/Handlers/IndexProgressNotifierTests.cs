using GSCode.Server.Logging;
using GSCode.Workspace.Indexing;
using Serilog.Events;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// `gscode.serverLogLevel: verbose` used to be byte-identical to `info`: the server contained no
/// Log.Verbose or Log.Debug call anywhere, so a setting whose description promised more detail
/// delivered none at all.
/// </summary>
public class IndexProgressNotifierTests
{
    [Theory]
    [InlineData("verbose", LogEventLevel.Verbose)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("warning", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    public void TheSettingSelectsTheLevel(string setting, LogEventLevel expected)
    {
        Assert.Equal(expected, ServerLogLevel.FromSetting(setting));
    }

    [Fact]
    public void VerboseIsBelowInformation_SoTheNewLinesOnlyAppearWhenAskedFor()
    {
        // Serilog orders Verbose < Debug < Information. Per-file timings sit at Verbose and slow
        // files at Debug, so both stay out of the default log and a slow file still stands out
        // once verbose is on.
        Assert.True(ServerLogLevel.FromSetting("verbose") < LogEventLevel.Debug);
        Assert.True(LogEventLevel.Debug < ServerLogLevel.FromSetting("info"));
    }

    [Fact]
    public void OffSilencesEverything()
    {
        // Above Fatal, so no event can reach it.
        Assert.True(ServerLogLevel.FromSetting("off") > LogEventLevel.Fatal);
    }

    [Fact]
    public void TheNullListenerImplementsPerFileReporting()
    {
        // The interface gained FileIndexed; tests and indexing-off paths use this listener, so a
        // missing implementation would break them rather than merely losing a log line.
        NullIndexProgressListener listener = NullIndexProgressListener.Instance;

        listener.FileIndexed(@"C:\bo3\share\raw\scripts\main.gsc", TimeSpan.FromMilliseconds(12), restoredFromCache: false);
    }
}
