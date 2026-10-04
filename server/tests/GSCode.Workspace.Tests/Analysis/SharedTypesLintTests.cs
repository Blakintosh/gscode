using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Typing;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The lint pass fills the shared flow-typing cache only when its caller asks it to.
///
/// The server's linter asks, so the inlay hints and hover after an edit read the walk the lints
/// already paid for. Every other caller must not: the perf sweep and the budget gate warm a file
/// with one pass and time a second, and a second pass reading the first one's answer would time a
/// cache hit and report the most expensive step in the pass as free.
/// </summary>
public class SharedTypesLintTests
{
    private const string Source = "function f()\n{\n    x = 1;\n}\n";

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ParseResult Lint(TestWorkspace workspace, bool shareTypes)
    {
        ParseResult result = workspace.Analyze(@"scripts\t.gsc");

        WorkspaceLints.LintsOnly(
            result,
            ScriptLanguage.Gsc,
            TestPaths.Raw(@"scripts\t.gsc"),
            workspace.Database,
            workspace.Resolver,
            BuiltinApiSet.Load(ApiDirectory),
            ObjectFields.Load(ApiDirectory),
            shareTypes: shareTypes);

        return result;
    }

    [Fact]
    public void TheServersLintPassFillsTheSharedCache()
    {
        using TestWorkspace workspace = TestWorkspace.Build([new TestFile(@"scripts\t.gsc", Source)]);

        ParseResult result = Lint(workspace, shareTypes: true);

        Assert.NotNull(FlowTyper.SharedFor(result));
    }

    [Fact]
    public void AnyOtherLintPassLeavesItAlone()
    {
        using TestWorkspace workspace = TestWorkspace.Build([new TestFile(@"scripts\t.gsc", Source)]);

        ParseResult result = Lint(workspace, shareTypes: false);

        Assert.Null(FlowTyper.SharedFor(result));
    }
}
