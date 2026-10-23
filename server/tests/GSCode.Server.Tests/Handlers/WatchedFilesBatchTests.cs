using GSCode.Core.Diagnostics;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A branch switch: many files changed on disk, reported as one <c>didChangeWatchedFiles</c>.
///
/// The handler queues the batch and returns, and the queue applies it with the changed scripts
/// re-indexed in parallel — so these drive the handler and then wait for the queue, and ask what a
/// sequential apply would have left: every changed record replaced from disk, every deleted one
/// gone, and in full mode every rewritten record linted again.
/// </summary>
public sealed class WatchedFilesBatchTests
{
    private const int Callers = 40;

    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    // Imports lib and uses nothing from it: an UnusedUsing hint, which only the cross-file lints raise.
    private static string CallerSource(int index)
    {
        return $"#using scripts\\lib;\n#namespace caller{index};\nfunction run()\n{{\n}}\n";
    }

    private static string CallerRelative(int index)
    {
        return $@"scripts\caller{index}.gsc";
    }

    private static ServerSettings Full => new() { WorkspaceIndexingMode = "full", RawPath = TestPaths.RawRoot };

    private sealed class Started : IDisposable
    {
        public required HandlerWorkspace Workspace { get; init; }

        public required WatchedFilesHandler Handler { get; init; }

        public void Dispose()
        {
            Workspace.Dispose();
        }
    }

    /// <summary>The workspace indexed and swept, and a handler wired as the server wires it.</summary>
    private static async Task<Started> StartAsync()
    {
        List<TestFile> files = [new TestFile(@"scripts\lib.gsc", LibSource)];
        for ( int index = 0; index < Callers; index++ )
        {
            files.Add(new TestFile(CallerRelative(index), CallerSource(index)));
        }

        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(files, mode: IndexingMode.Full);
        ServerSettings settings = Full;
        DocumentLinter linter = new(workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        DiagnosticsPublisher publisher = new(DiscardingDiagnosticsSink.Instance);
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(workspace.Database, workspace.Documents, publisher, settings);
        WorkspaceLintSweep sweep = new(workspace.Database, workspace.Documents, workspace.Indexer, linter);
        DependentDiagnosticsRefresher dependents = new(
            workspace.Documents, publisher, linter, workspace.Database, sweep, workspaceDiagnostics,
            NullCodeLensRefreshSink.Instance, settings);
        await sweep.RunFullSweepAsync(CancellationToken.None);

        WatchedFilesHandler handler = new(
            new WatchedFileUpdater(workspace.Database, workspace.Indexer),
            workspace.Database, workspace.Documents, dependents, workspaceDiagnostics, sweep, settings);
        return new Started { Workspace = workspace, Handler = handler };
    }

    private static FileEvent EventFor(string relativePath, FileChangeType type)
    {
        return new FileEvent { Uri = DocumentUri.FromFileSystemPath(TestPaths.Raw(relativePath)), Type = type };
    }

    private static async Task ApplyAsync(WatchedFilesHandler handler, List<FileEvent> events)
    {
        await handler.Handle(new DidChangeWatchedFilesParams { Changes = new Container<FileEvent>(events) }, CancellationToken.None);
        await handler.WhenIdleAsync();
    }

    [Fact]
    public async Task EveryFileInABatchIsReindexedAndLintedAgain()
    {
        using Started started = await StartAsync();
        HandlerWorkspace workspace = started.Workspace;
        WatchedFilesHandler handler = started.Handler;

        List<FileEvent> events = [];
        for ( int index = 0; index < Callers; index++ )
        {
            workspace.Files.AddFile(
                TestPaths.Raw(CallerRelative(index)), CallerSource(index) + $"function added{index}()\n{{\n}}\n");
            events.Add(EventFor(CallerRelative(index), FileChangeType.Changed));
        }

        await ApplyAsync(handler, events);

        for ( int index = 0; index < Callers; index++ )
        {
            Assert.True(workspace.Database.TryGetAnyRecord(TestPaths.Raw(CallerRelative(index)), out ScriptRecord record));
            Assert.Contains(record.Functions, function => function.KeyName == $"added{index}");
            Assert.Contains(record.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.UnusedUsing);
        }
    }

    [Fact]
    public async Task ADeletionInABatchOfChangesRemovesItsRecord()
    {
        using Started started = await StartAsync();
        HandlerWorkspace workspace = started.Workspace;
        WatchedFilesHandler handler = started.Handler;

        workspace.Files.RemoveFile(TestPaths.Raw(CallerRelative(0)));
        List<FileEvent> events = [EventFor(CallerRelative(0), FileChangeType.Deleted)];
        for ( int index = 1; index < Callers; index++ )
        {
            workspace.Files.AddFile(TestPaths.Raw(CallerRelative(index)), "// switched\n" + CallerSource(index));
            events.Add(EventFor(CallerRelative(index), FileChangeType.Changed));
        }

        await ApplyAsync(handler, events);

        Assert.False(workspace.Database.TryGetAnyRecord(TestPaths.Raw(CallerRelative(0)), out _));
        Assert.True(workspace.Database.TryGetAnyRecord(TestPaths.Raw(CallerRelative(1)), out _));
    }

    [Fact]
    public async Task AFileCreatedInABatchIsIndexed()
    {
        using Started started = await StartAsync();
        HandlerWorkspace workspace = started.Workspace;
        WatchedFilesHandler handler = started.Handler;

        // Created and then written before the queue reached it: one path, two events.
        const string created = @"scripts\fresh.gsc";
        workspace.Files.AddFile(TestPaths.Raw(created), "#namespace fresh;\nfunction made()\n{\n}\n");
        await ApplyAsync(handler, [EventFor(created, FileChangeType.Created), EventFor(created, FileChangeType.Changed)]);

        Assert.True(workspace.Database.TryGetAnyRecord(TestPaths.Raw(created), out ScriptRecord record));
        Assert.Contains(record.Functions, function => function.KeyName == "made");
    }
}
