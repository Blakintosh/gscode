using GSCode.Server.Configuration;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GSCode.Server.Tests.Configuration;

/// <summary>
/// The change detector behind the inlay-hint refresh.
///
/// Hints are computed per request and then cached by the client, so toggling a family is invisible
/// until something else invalidates the document — which reads as the setting not working, and
/// worse when turning one OFF, because the stale hints stay on screen. `ConfigurationHandler` asks
/// for `workspace/inlayHint/refresh` when this value moves, and clients push their WHOLE
/// configuration on any settings edit, so it has to move on exactly the three families and nothing
/// else.
/// </summary>
public class InlayFamiliesTests
{
    [Fact]
    public void EachFamilyMovesIt()
    {
        ServerSettings settings = new();
        string atStart = settings.InlayFamilies;

        settings.InlayInferredTypes = !settings.InlayInferredTypes;
        string afterTypes = settings.InlayFamilies;
        Assert.NotEqual(atStart, afterTypes);

        settings.InlayParameterNames = !settings.InlayParameterNames;
        string afterParameters = settings.InlayFamilies;
        Assert.NotEqual(afterTypes, afterParameters);

        settings.InlayMacroParameterNames = !settings.InlayMacroParameterNames;
        Assert.NotEqual(afterParameters, settings.InlayFamilies);
    }

    [Fact]
    public void AnUnrelatedSettingDoesNot()
    {
        // A refresh per font-size edit is the cost of getting this wrong.
        ServerSettings settings = new();
        string before = settings.InlayFamilies;

        settings.ServerLogLevel = "debug";
        settings.FormatPadParens = false;
        settings.DiagnosticsScope = "open";

        Assert.Equal(before, settings.InlayFamilies);
    }

    [Fact]
    public void APushThatSaysNothingAboutInlayHintsChangesNothing()
    {
        ServerSettings settings = new();
        string before = settings.InlayFamilies;

        settings.Apply(JToken.Parse("""{ "gscode": { "serverLogLevel": "debug" } }"""));

        Assert.Equal(before, settings.InlayFamilies);
    }

    [Theory]
    [InlineData("""{ "gscode": { "inlayHints.macroParameterNames": true } }""")]
    [InlineData("""{ "gscode": { "inlayHints": { "macroParameterNames": true } } }""")]
    public void TheMacroFamilyIsReadFromEitherKeyForm(string payload)
    {
        // Both forms because a client may send the settings flat or nested, as the other
        // inlayHints.* keys already accept.
        ServerSettings settings = new();
        Assert.False(settings.InlayMacroParameterNames);

        settings.Apply(JToken.Parse(payload));

        Assert.True(settings.InlayMacroParameterNames);
        Assert.Contains("macroParameters=on", settings.InlayFamilies, StringComparison.Ordinal);
    }
}
