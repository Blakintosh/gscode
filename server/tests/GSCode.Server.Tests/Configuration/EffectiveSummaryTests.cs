using GSCode.Server.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// The startup line naming the settings that shape behaviour.
///
/// Nearly every "why is it doing that" turns out to be one of these — indexing off, the cache
/// serving a stale record, diagnostics scoped to open files, raw resolution disabled — and none
/// is visible from the symptom alone. It is logged at Information so it is already in a log
/// somebody attaches to a bug report, rather than something they have to be asked to reproduce.
/// </summary>
public class EffectiveSummaryTests
{
    [Theory]
    [InlineData("indexing=")]
    [InlineData("cache=")]
    [InlineData("diagnostics=")]
    [InlineData("raw=")]
    [InlineData("rawWarning=")]
    [InlineData("codeLens=")]
    [InlineData("log=")]
    public void EverySettingWorthCheckingFirstIsNamed(string field)
    {
        Assert.Contains(field, new ServerSettings().EffectiveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void BooleansReadAsOnOrOff()
    {
        // "cache=True" is C# leaking into something a person reads.
        ServerSettings settings = new();

        Assert.Contains("cache=on", settings.EffectiveSummary, StringComparison.Ordinal);

        settings.EnableWorkspaceCache = false;
        Assert.Contains("cache=off", settings.EffectiveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingThatMattersChangesTheSummary()
    {
        // The summary doubles as the change detector, so this is what stops a meaningful change
        // from going unlogged.
        ServerSettings settings = new();
        string before = settings.EffectiveSummary;

        settings.Apply(JToken.Parse("""{ "gscode": { "workspaceIndexingMode": "off" } }"""));

        Assert.NotEqual(before, settings.EffectiveSummary);
        Assert.Contains("indexing=off", settings.EffectiveSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingThatDoesNotMatterLeavesItAlone()
    {
        // Clients push their WHOLE configuration on any settings edit, so an unrelated change
        // must not produce a "settings changed" line.
        ServerSettings settings = new();
        string before = settings.EffectiveSummary;

        settings.Apply(JToken.Parse("""{ "gscode": { "completion": { "fieldScope": "all" } } }"""));

        Assert.Equal(before, settings.EffectiveSummary);
    }

    [Fact]
    public void ReapplyingTheSameConfigurationIsNotAChange()
    {
        ServerSettings settings = new();
        JToken configuration = JToken.Parse("""{ "gscode": { "workspaceIndexingMode": "full" } }""");

        settings.Apply(configuration);
        string after = settings.EffectiveSummary;
        settings.Apply(configuration);

        Assert.Equal(after, settings.EffectiveSummary);
    }
}
