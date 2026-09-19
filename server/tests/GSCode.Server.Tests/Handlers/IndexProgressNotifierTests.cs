using GSCode.Server.Handlers;
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

    [Fact]
    public void OnlyOneOfManyThreadsWinsTheThrottleSlot()
    {
        // Progressed is called from inside the indexer's Parallel.ForEachAsync, on every worker
        // thread. The shared Stopwatch this replaced was not thread-safe, and its check-then-Restart
        // was not atomic, so several workers passed the gate together and the "at most one per
        // 40 ms" contract was not enforced at all.
        ProgressThrottle throttle = new(intervalMilliseconds: 40);

        int winners = 0;
        // Distinct, increasing counts, so the ordering half never refuses one and what is being
        // measured is the interval claim alone.
        Parallel.For(0, 64, index =>
        {
            if ( throttle.ShouldSend(index + 1, isFinal: false) )
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
    }

    [Fact]
    public void ACountOlderThanOneAlreadySentIsDropped()
    {
        // A parallel walk reports out of ORDER, so a slower worker's older number can land after a
        // faster one's. Sending it made the status-bar counter visibly run backwards.
        // No interval, so what is measured here is the ordering half alone.
        ProgressThrottle throttle = new(intervalMilliseconds: 0);

        Assert.True(throttle.ShouldSend(100, isFinal: false));
        Assert.False(throttle.ShouldSend(80, isFinal: false));
        Assert.True(throttle.ShouldSend(120, isFinal: false));
    }

    [Fact]
    public void TheFinalCountAlwaysGoesAndNothingFollowsItBackwards()
    {
        ProgressThrottle throttle = new(intervalMilliseconds: 0);

        Assert.True(throttle.ShouldSend(900, isFinal: false));
        Assert.True(throttle.ShouldSend(1000, isFinal: true));
        Assert.False(throttle.ShouldSend(950, isFinal: false));
    }
}
