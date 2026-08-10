using GSCode.Workspace.Api;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

public class RawWriteGuardTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static StockScripts Stock => StockScripts.Load(ApiDirectory);

    // A real entry from the bundled list.
    private const string StockPath = @"scripts\codescripts\struct.gsc";
    private const string OwnPath = @"scripts\mymod\my_feature.gsc";

    [Fact]
    public void StockList_LoadsAndIgnoresCommentLines()
    {
        StockScripts scripts = Stock;

        Assert.True(scripts.Count > 1000);
        Assert.True(scripts.Contains(StockPath));
        Assert.False(scripts.Contains(OwnPath));
    }

    [Fact]
    public void StockLookup_IgnoresSlashStyleAndCasing()
    {
        StockScripts scripts = Stock;

        Assert.True(scripts.Contains("scripts/codescripts/struct.gsc"));
        Assert.True(scripts.Contains(@"Scripts\CodeScripts\Struct.gsc"));
    }

    [Fact]
    public void ParseMode_FallsBackToStockForAnythingUnrecognised()
    {
        Assert.Equal(RawFileWarningMode.Off, RawWriteGuard.ParseMode("off"));
        Assert.Equal(RawFileWarningMode.All, RawWriteGuard.ParseMode("ALL"));
        Assert.Equal(RawFileWarningMode.Stock, RawWriteGuard.ParseMode("stock"));
        Assert.Equal(RawFileWarningMode.Stock, RawWriteGuard.ParseMode("nonsense"));
    }

    [Fact]
    public void Off_NeverWarns_EvenForAStockScript()
    {
        bool warn = RawWriteGuard.ShouldWarn(
            RawFileWarningMode.Off, ResolutionContext.RawContext, StockPath, Stock);

        Assert.False(warn);
    }

    [Fact]
    public void Stock_WarnsForStockScriptsOnly()
    {
        Assert.True(RawWriteGuard.ShouldWarn(
            RawFileWarningMode.Stock, ResolutionContext.RawContext, StockPath, Stock));
        Assert.False(RawWriteGuard.ShouldWarn(
            RawFileWarningMode.Stock, ResolutionContext.RawContext, OwnPath, Stock));
    }

    [Fact]
    public void All_WarnsForAnyRawFile()
    {
        bool warn = RawWriteGuard.ShouldWarn(
            RawFileWarningMode.All, ResolutionContext.RawContext, OwnPath, Stock);

        Assert.True(warn);
    }

    [Fact]
    public void ModAndWorkspaceFiles_NeverWarn()
    {
        // Shadowing a stock script from a mod is the correct workflow, so it must stay silent
        // even though the relative path matches a stock entry and the mode is "all".
        Assert.False(RawWriteGuard.ShouldWarn(
            RawFileWarningMode.All, ResolutionContext.ForMod("mod_a"), StockPath, Stock));
        Assert.False(RawWriteGuard.ShouldWarn(
            RawFileWarningMode.All, ResolutionContext.ForWorkspace(@"C:\work"), StockPath, Stock));
    }
}
