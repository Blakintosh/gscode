using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The closed files a full-mode re-lint reaches after an edit changes a file's exports, on both
/// dialect families. On a merge dialect a function still has a declared namespace — the file stem
/// — but calls to it are keyed with none, so the dependents have to be found under the key the
/// references were actually indexed with, not one rebuilt from the declaration.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class ClosedDependentsDialectTests
{
    private const string LibRelativePath = @"maps\lib.gsc";
    private const string CallerRelativePath = @"maps\caller.gsc";

    private static async Task<HashSet<string>> DependentsOnAsync(GameProfile profile, string libSource, string callerSource)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [
                new TestFile(LibRelativePath, libSource),
                new TestFile(CallerRelativePath, callerSource),
            ],
            profile);

        Assert.True(workspace.Database.TryGetAnyRecord(TestPaths.Raw(LibRelativePath), out ScriptRecord origin));
        return DependentDiagnosticsRefresher.ClosedDependentsOf(origin, workspace.Database.Gsc, workspace.Documents);
    }

    [Fact]
    public async Task AMergeDialectCaller_IsADependent()
    {
        HashSet<string> dependents = await DependentsOnAsync(
            GameProfile.Cod4,
            "helper()\n{\n}\n",
            "#include maps\\lib;\n\nrun()\n{\n    helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(TestPaths.Raw(CallerRelativePath)), dependents);
    }

    [Fact]
    public async Task AMergeDialectPathCall_IsADependent()
    {
        HashSet<string> dependents = await DependentsOnAsync(
            GameProfile.Cod4,
            "helper()\n{\n}\n",
            "run()\n{\n    maps\\lib::helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(TestPaths.Raw(CallerRelativePath)), dependents);
    }

    [Fact]
    public async Task ANamespaceDialectCaller_IsStillADependent()
    {
        HashSet<string> dependents = await DependentsOnAsync(
            GameProfile.BlackOps3,
            "#namespace lib;\nfunction helper()\n{\n}\n",
            "#using maps\\lib;\n#namespace game;\nfunction run()\n{\n    lib::helper();\n}\n");

        Assert.Contains(PathUtil.NormalizeAbsolute(TestPaths.Raw(CallerRelativePath)), dependents);
    }
}
