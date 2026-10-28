using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// An edit waits three times its document's last analysis before re-analysing, never less than the
/// floor a word's typing falls inside and never more than the budget every analysis is held to.
/// </summary>
public sealed class AnalysisTimingTests
{
    [Theory]
    [InlineData(0, AnalysisTiming.DebounceMilliseconds)]
    [InlineData(10, AnalysisTiming.MinimumDebounceMilliseconds)]
    [InlineData(32, AnalysisTiming.MinimumDebounceMilliseconds)]
    [InlineData(60, 180)]
    [InlineData(83, 249)]
    [InlineData(400, AnalysisTiming.DebounceMilliseconds)]
    public void TheWaitFollowsTheLastAnalysis_WithinTheFloorAndTheBudget(double lastAnalysis, int expected)
    {
        Assert.Equal(expected, AnalysisTiming.DebounceFor(lastAnalysis));
    }
}
