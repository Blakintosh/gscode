using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Which files report problems. ScriptRecord.Diagnostics was written on every index and never
/// read, so a broken script stayed invisible until somebody happened to open it.
/// </summary>
public class DiagnosticsScopeTests
{
    [Theory]
    [InlineData("open", DiagnosticsScope.Open)]
    [InlineData("workspace", DiagnosticsScope.Workspace)]
    [InlineData("all", DiagnosticsScope.All)]
    [InlineData("Workspace", DiagnosticsScope.Workspace)]
    [InlineData("ALL", DiagnosticsScope.All)]
    public void TheSettingMapsToAScope(string setting, DiagnosticsScope expected)
    {
        Assert.Equal(expected, WorkspaceDiagnosticsPublisher.ScopeFromSetting(setting));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("everything")]
    public void AnUnrecognisedSettingKeepsTheDefault(string setting)
    {
        // Not Open: a typo in a setting should not silently switch problems off.
        Assert.Equal(DiagnosticsScope.Workspace, WorkspaceDiagnosticsPublisher.ScopeFromSetting(setting));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("mod:mymod")]
    [InlineData("workspace:C:\\proj")]
    public void OpenScopePublishesNothingFromTheIndex(string contextId)
    {
        // Open documents are the sync handler's job, so this publisher has nothing to add.
        Assert.False(WorkspaceDiagnosticsPublisher.IsInScope(DiagnosticsScope.Open, contextId));
    }

    [Fact]
    public void WorkspaceScopeCoversYourOwnFilesButNotStock()
    {
        // "raw" is the game's shipped scripts: read-only, and thousands of diagnostics nobody
        // asked for. Everything else is something the user can actually fix.
        Assert.True(WorkspaceDiagnosticsPublisher.IsInScope(DiagnosticsScope.Workspace, "mod:mymod"));
        Assert.True(WorkspaceDiagnosticsPublisher.IsInScope(DiagnosticsScope.Workspace, @"workspace:C:\proj"));
        Assert.False(WorkspaceDiagnosticsPublisher.IsInScope(DiagnosticsScope.Workspace, "raw"));
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("mod:mymod")]
    [InlineData("workspace:C:\\proj")]
    public void AllScopeCoversEverything(string contextId)
    {
        Assert.True(WorkspaceDiagnosticsPublisher.IsInScope(DiagnosticsScope.All, contextId));
    }
}
