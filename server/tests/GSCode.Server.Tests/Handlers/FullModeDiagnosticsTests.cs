using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// <c>workspaceIndexingMode: full</c> after startup: a closed file keeps reporting its cross-file
/// problems through everything that rewrites its record.
///
/// The startup sweep was always right (<see cref="WorkspaceLintSweepTests"/>). Three later paths
/// stored the parse diagnostics alone over its result: analysing an open document, a change on disk,
/// and a workspace folder added mid-session. Each case here failed before the fix that names it.
/// </summary>
public sealed class FullModeDiagnosticsTests
{
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    // Imports lib and uses nothing from it: an UnusedUsing hint, which only the cross-file lints raise.
    private const string CallerSource = "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n}\n";

    private static string CallerPath => TestPaths.Raw(@"scripts\caller.gsc");

    /// <summary>
    /// Full mode over the standard raw root. The folder handler rebuilds the roots from settings, so
    /// they have to name the raw root the workspace was built over or nothing resolves after it.
    /// </summary>
    private static ServerSettings Full => new() { WorkspaceIndexingMode = "full", RawPath = TestPaths.RawRoot };

    private sealed class Server
    {
        public required DocumentLinter Linter { get; init; }

        public required WorkspaceLintSweep Sweep { get; init; }

        public required WorkspaceDiagnosticsPublisher WorkspaceDiagnostics { get; init; }

        public required DependentDiagnosticsRefresher Dependents { get; init; }
    }

    private static Server Wire(HandlerWorkspace workspace, ServerSettings settings)
    {
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        DiagnosticsPublisher publisher = new(DiscardingDiagnosticsSink.Instance);
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(
            workspace.Database, workspace.Documents, publisher, settings);
        WorkspaceLintSweep sweep = new(workspace.Database, workspace.Documents, workspace.Indexer, linter);

        return new Server
        {
            Linter = linter,
            Sweep = sweep,
            WorkspaceDiagnostics = workspaceDiagnostics,
            Dependents = new DependentDiagnosticsRefresher(
                workspace.Documents, publisher, linter, workspace.Database, sweep, workspaceDiagnostics,
                NullCodeLensRefreshSink.Instance, settings),
        };
    }

    private sealed class Started : IDisposable
    {
        public required HandlerWorkspace Workspace { get; init; }

        public required Server Server { get; init; }

        public void Dispose()
        {
            Workspace.Dispose();
        }
    }

    /// <summary>Indexed and swept, as a full-mode startup leaves it.</summary>
    private static async Task<Started> StartedAsync()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\lib.gsc", LibSource), new TestFile(@"scripts\caller.gsc", CallerSource)],
            mode: IndexingMode.Full);
        Server server = Wire(workspace, Full);
        await server.Sweep.RunFullSweepAsync(CancellationToken.None);

        Assert.True(ReportsUnusedUsing(workspace, CallerPath), "the startup sweep is the precondition");
        return new Started { Workspace = workspace, Server = server };
    }

    private static bool ReportsUnusedUsing(HandlerWorkspace workspace, string path)
    {
        return workspace.Database.TryGetAnyRecord(path, out ScriptRecord record)
            && record.Diagnostics.Any(diagnostic => diagnostic.Code == GscDiagnosticCode.UnusedUsing);
    }

    [Fact]
    public async Task OpeningAndClosingAFile_KeepsItsCrossFileProblems()
    {
        using Started started = await StartedAsync();
        HandlerWorkspace workspace = started.Workspace;
        Server server = started.Server;

        // What TextSyncHandler.AnalyzeAndPublish does for an open document: analyse, lint, commit,
        // and in full mode keep the published set on the record.
        OpenDocument document = workspace.Open(@"scripts\caller.gsc");
        ParseResult result = workspace.Documents.Analyze(document);
        ImmutableArray<Diagnostic> published = server.Linter.Analyze(document, result);
        ResolutionContext context = workspace.ResolverHolder.Current.GetContext(CallerPath);
        ScriptRecord committed = workspace.Database.Commit(
            result, context, isDirty: true, workspace.ResolverHolder.Current.GetScriptRelativePath(CallerPath, context));

        // The commit alone is the bug: it stores the parse diagnostics, and the record is what a
        // closed file reports.
        Assert.False(ReportsUnusedUsing(workspace, CallerPath));

        Assert.True(server.Sweep.KeepOnRecord(committed, published));
        workspace.Documents.Close(CallerPath);

        Assert.True(ReportsUnusedUsing(workspace, CallerPath));
    }

    [Fact]
    public async Task AFileChangedOnDisk_IsLintedAgain()
    {
        using Started started = await StartedAsync();
        HandlerWorkspace workspace = started.Workspace;
        Server server = started.Server;

        WatchedFilesHandler handler = new(
            new WatchedFileUpdater(workspace.Database, workspace.Indexer),
            workspace.Database,
            workspace.Documents,
            server.Dependents,
            server.WorkspaceDiagnostics,
            server.Sweep,
            Full);

        // A branch switch or another tool: a comment added, the unused #using kept.
        workspace.Files.AddFile(CallerPath, "// edited\n" + CallerSource);
        await handler.Handle(
            new DidChangeWatchedFilesParams
            {
                Changes = new Container<FileEvent>(
                    new FileEvent { Uri = DocumentUri.FromFileSystemPath(CallerPath), Type = FileChangeType.Changed }),
            },
            CancellationToken.None);

        Assert.True(ReportsUnusedUsing(workspace, CallerPath));
    }

    [Fact]
    public async Task AFileChangedOnDisk_InPartialMode_KeepsParseDiagnosticsOnly()
    {
        // The other side of the line: partial promises cross-file problems for open files only.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"scripts\lib.gsc", LibSource), new TestFile(@"scripts\caller.gsc", CallerSource)],
            mode: IndexingMode.Partial);
        ServerSettings partial = new();
        Server server = Wire(workspace, partial);

        WatchedFilesHandler handler = new(
            new WatchedFileUpdater(workspace.Database, workspace.Indexer),
            workspace.Database,
            workspace.Documents,
            server.Dependents,
            server.WorkspaceDiagnostics,
            server.Sweep,
            partial);

        workspace.Files.AddFile(CallerPath, "// edited\n" + CallerSource);
        await handler.Handle(
            new DidChangeWatchedFilesParams
            {
                Changes = new Container<FileEvent>(
                    new FileEvent { Uri = DocumentUri.FromFileSystemPath(CallerPath), Type = FileChangeType.Changed }),
            },
            CancellationToken.None);

        Assert.False(ReportsUnusedUsing(workspace, CallerPath));
    }

    [Fact]
    public async Task AFolderAddedMidSession_IsSwept()
    {
        using Started started = await StartedAsync();
        HandlerWorkspace workspace = started.Workspace;
        Server server = started.Server;

        // A workspace folder of its own, outside the raw root: the folder is the subject here.
        const string folder = @"c:\work\addon";
        string added = Path.Combine(folder, "addon.gsc");
        workspace.Files.AddFile(added, CallerSource.Replace("game", "addon", StringComparison.Ordinal));

        WorkspaceFoldersHandler handler = new(
            workspace.ResolverHolder,
            Full,
            workspace.Files,
            workspace.Database,
            workspace.Indexer,
            workspace.Documents,
            server.Sweep,
            server.WorkspaceDiagnostics,
            server.Dependents);

        await handler.Handle(
            new DidChangeWorkspaceFoldersParams
            {
                Event = new WorkspaceFoldersChangeEvent
                {
                    Added = new Container<WorkspaceFolder>(
                        new WorkspaceFolder { Uri = DocumentUri.FromFileSystemPath(folder), Name = "addon" }),
                    Removed = new Container<WorkspaceFolder>(),
                },
            },
            CancellationToken.None);

        Assert.True(
            workspace.Database.TryGetAnyRecord(added, out ScriptRecord _),
            "the added folder's file is indexed");
        Assert.True(ReportsUnusedUsing(workspace, added));
    }
}
