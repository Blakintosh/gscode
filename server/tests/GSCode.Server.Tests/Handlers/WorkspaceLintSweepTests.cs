using GSCode.Core.Diagnostics;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// F7: <c>workspaceIndexingMode: full</c> — the cross-file lints, run over a CLOSED file's
/// stored record rather than only over open documents.
/// </summary>
public sealed class WorkspaceLintSweepTests
{
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    private const string CallerSource = "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n";

    private const string OtherCallerSource = "#using scripts\\lib;\n#namespace game2;\nfunction run2()\n{\n}\n";

    /// <summary>Indexed the way startup indexes: partial, so only parse-level diagnostics are stored.</summary>
    private static Task<HandlerWorkspace> BuildAsync(params TestFile[] files)
    {
        return HandlerWorkspace.BuildAsync(files, mode: IndexingMode.Partial);
    }

    private static WorkspaceLintSweep SweepOver(HandlerWorkspace workspace)
    {
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        return new WorkspaceLintSweep(workspace.Database, workspace.Documents, workspace.Indexer, linter);
    }

    [Fact]
    public async Task BeforeTheSweep_AClosedFilesStoredDiagnosticsAreParseLevelOnly()
    {
        using HandlerWorkspace workspace = await BuildAsync(
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource));

        string callerPath = TestPaths.Raw(@"scripts\caller.gsc");
        Assert.True(workspace.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.DoesNotContain(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RunFullSweepAsync_AddsTheCrossFileLintToTheClosedRecord()
    {
        using HandlerWorkspace workspace = await BuildAsync(
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource));
        string callerPath = TestPaths.Raw(@"scripts\caller.gsc");

        LintSweepOutcome outcome = await SweepOver(workspace).RunFullSweepAsync(CancellationToken.None);

        Assert.True(outcome.Linted > 0);
        Assert.True(workspace.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.Contains(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RunFullSweepAsync_SkipsAnOpenDocument_LeavingItsRecordUntouched()
    {
        using HandlerWorkspace workspace = await BuildAsync(
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource));
        string callerPath = TestPaths.Raw(@"scripts\caller.gsc");

        // Opened but not analysed: the sweep must stand down on the open state alone.
        workspace.Documents.Open(callerPath, workspace.Files.ReadAllText(callerPath), version: 1);

        await SweepOver(workspace).RunFullSweepAsync(CancellationToken.None);

        // Untouched, not merely lacking the lint: the sweep must not have written to it at all,
        // since the open document's own live-analysis path is what owns its diagnostics now.
        Assert.True(workspace.Database.TryGetAnyRecord(callerPath, out ScriptRecord record));
        Assert.DoesNotContain(record.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task RelintClosedFilesAsync_UpgradesOnlyTheNamedFile()
    {
        using HandlerWorkspace workspace = await BuildAsync(
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
            new TestFile(@"scripts\other_caller.gsc", OtherCallerSource));
        string callerPath = TestPaths.Raw(@"scripts\caller.gsc");
        string otherPath = TestPaths.Raw(@"scripts\other_caller.gsc");

        LintSweepOutcome outcome = await SweepOver(workspace).RelintClosedFilesAsync([callerPath], CancellationToken.None);

        Assert.Equal(1, outcome.Linted);
        Assert.True(workspace.Database.TryGetAnyRecord(callerPath, out ScriptRecord relinted));
        Assert.Contains(relinted.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);

        Assert.True(workspace.Database.TryGetAnyRecord(otherPath, out ScriptRecord untouched));
        Assert.DoesNotContain(untouched.Diagnostics, d => d.Code == GscDiagnosticCode.UnusedUsing);
    }
}
