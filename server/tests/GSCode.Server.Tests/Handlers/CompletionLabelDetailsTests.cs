using GSCode.Server.Handlers;
using GSCode.Workspace.Completion;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Where a function's parameter list is presented, which depends on the client.
///
/// LSP 3.17's <c>CompletionItem.labelDetails</c> is the field made for it — dimmed text beside the
/// label, leaving the label itself as the thing the editor filters and sorts on. A client that does
/// not advertise support would simply drop the field, so there the same text is folded into the
/// label instead: visibly worse, but not silently gone.
/// </summary>
public class CompletionLabelDetailsTests
{
    private static CompletionEntry Function(string label, string labelDetail, string filterText = "")
    {
        return new CompletionEntry(
            label, CompletionKind.Function, "function", label + "($0)", FilterText: filterText, LabelDetail: labelDetail);
    }

    [Fact]
    public void WhenSupported_TheLabelIsLeftAlone()
    {
        CompletionHandler.LabelParts parts = CompletionHandler.SplitLabel(
            Function("get_players", "( team, alive )"), labelDetailsSupported: true);

        Assert.Equal("get_players", parts.Label);
        Assert.Equal("( team, alive )", parts.Detail);

        // Nothing to compensate for: filtering keys off the label, which is still just the name.
        Assert.Null(parts.FilterText);
    }

    [Fact]
    public void WhenUnsupported_ItIsFoldedInAndFilteringIsPinnedBack()
    {
        CompletionHandler.LabelParts parts = CompletionHandler.SplitLabel(
            Function("get_players", "( team, alive )"), labelDetailsSupported: false);

        Assert.Equal("get_players( team, alive )", parts.Label);
        Assert.Null(parts.Detail);

        // The pin is the point: without it the editor would match "team" against this entry.
        Assert.Equal("get_players", parts.FilterText);
    }

    [Fact]
    public void AnEntrysOwnFilterTextIsNeverOverwritten()
    {
        // An imported function filters on its QUALIFIER so that typing the namespace finds it, and
        // a directive filters without its '#'. Neither wants replacing by the fallback's pin.
        CompletionHandler.LabelParts parts = CompletionHandler.SplitLabel(
            Function("util::get_players", "( team )", filterText: "util::get_players"),
            labelDetailsSupported: false);

        Assert.Equal("util::get_players", parts.FilterText);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEntryWithNoParametersIsUnchangedEitherWay(bool supported)
    {
        // Keywords, fields, literals and path segments carry no signature, so neither branch may
        // touch them.
        CompletionEntry keyword = new("level", CompletionKind.Variable);

        CompletionHandler.LabelParts parts = CompletionHandler.SplitLabel(keyword, supported);

        Assert.Equal("level", parts.Label);
        Assert.Null(parts.Detail);
        Assert.Null(parts.FilterText);
    }
}
