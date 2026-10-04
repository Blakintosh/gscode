using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Diagnostic = GSCode.Core.Diagnostics.Diagnostic;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// <c>workspaceIndexingMode: off</c> and <c>partial</c> against what <c>client/package.json</c>
/// promises for each; <see cref="FullModeDiagnosticsTests"/> is the <c>full</c> half.
///
/// - off: nothing is indexed, so the checks that need the index (unresolved calls, unused imports,
///   most missing-import checks) stand down, while the ones that need only the file and the disk (a
///   <c>#using</c> naming no file, a duplicate import) still report.
/// - partial: everything is indexed for navigation; the cross-file lints run for OPEN files, and a
///   closed file reports the problems parsing finds, including after a folder change.
/// </summary>
public sealed class IndexingModeContractTests
{
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    // A #using naming no file, and the same #using twice.
    private const string CallerSource =
        "#using scripts\\lib;\n#using scripts\\missing;\n#using scripts\\lib;\n#namespace game;\n"
        + "function run()\n{\n}\n";

    // Imports lib, uses nothing from it, and calls a function nothing declares. Kept apart from the
    // file above: an unresolvable #using rightly stands the unresolved-call check down (ImportGate),
    // and the point here is the INDEX gate, not that one.
    private const string CallsSource =
        "#using scripts\\lib;\n#namespace calls;\nfunction run()\n{\n\tnosuch();\n}\n";

    private const string AddedFolder = @"c:\work\addon";

    private static TestFile[] Files =>
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
            new TestFile(@"scripts\calls.gsc", CallsSource),
        ];

    private static ServerSettings SettingsFor(string mode)
    {
        // The folder handler rebuilds the roots from settings, so they name the workspace's raw root.
        return new ServerSettings { WorkspaceIndexingMode = mode, RawPath = TestPaths.RawRoot };
    }

    private static ImmutableArray<Diagnostic> LintOpen(HandlerWorkspace workspace, string relativePath)
    {
        OpenDocument document = workspace.Open(relativePath);
        ParseResult result = workspace.Documents.Analyze(document);
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        return linter.Analyze(document, result);
    }

    private static bool Has(ImmutableArray<Diagnostic> diagnostics, GscDiagnosticCode code)
    {
        return diagnostics.Any(diagnostic => diagnostic.Code == code);
    }

    /// <summary>
    /// 5013 or 5014: which one a bare call to nothing earns depends on whether the name could be a
    /// builtin, and either says the same thing here — the index answered "nothing declares it".
    /// </summary>
    private static bool ReportsUnresolvedCall(ImmutableArray<Diagnostic> diagnostics)
    {
        return Has(diagnostics, GscDiagnosticCode.ScriptFunctionNotFound)
            || Has(diagnostics, GscDiagnosticCode.BuiltinFunctionNotFound);
    }

    private static WorkspaceFoldersHandler FolderHandler(
        HandlerWorkspace workspace, ServerSettings settings, IDiagnosticsSink sink)
    {
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);
        DiagnosticsPublisher publisher = new(sink);
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(
            workspace.Database, workspace.Documents, publisher, settings);
        WorkspaceLintSweep sweep = new(workspace.Database, workspace.Documents, workspace.Indexer, linter);
        DependentDiagnosticsRefresher dependents = new(
            workspace.Documents, publisher, linter, workspace.Database, sweep, workspaceDiagnostics,
            NullCodeLensRefreshSink.Instance, settings);

        return new WorkspaceFoldersHandler(
            workspace.ResolverHolder, settings, workspace.Files, workspace.Database, workspace.Indexer,
            workspace.Documents, sweep, workspaceDiagnostics, dependents);
    }

    private static DidChangeWorkspaceFoldersParams FolderChange(string? added, string? removed)
    {
        static Container<WorkspaceFolder> Of(string? folder)
        {
            return folder is null
                ? new Container<WorkspaceFolder>()
                : new Container<WorkspaceFolder>(
                    new WorkspaceFolder { Uri = DocumentUri.FromFileSystemPath(folder), Name = "addon" });
        }

        return new DidChangeWorkspaceFoldersParams
        {
            Event = new WorkspaceFoldersChangeEvent { Added = Of(added), Removed = Of(removed) },
        };
    }

    // --- off ---------------------------------------------------------------------------------

    [Fact]
    public async Task Off_IndexesNothing_AndNeverCallsTheIndexComplete()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Off);

        Assert.False(workspace.Database.TryGetAnyRecord(TestPaths.Raw(@"scripts\lib.gsc"), out ScriptRecord _));

        // The gate the index-only checks stand behind; were it true here, every cross-file call in
        // an open file would be reported missing.
        Assert.False(workspace.Database.HasCompletedIndex);
    }

    [Fact]
    public async Task Off_AnOpenFile_GetsTheFileAndDiskChecks_ButNotTheIndexChecks()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Off);

        ImmutableArray<Diagnostic> caller = LintOpen(workspace, @"scripts\caller.gsc");
        ImmutableArray<Diagnostic> calls = LintOpen(workspace, @"scripts\calls.gsc");

        Assert.True(Has(caller, GscDiagnosticCode.UsingNotFound), "5009 reads the disk");
        Assert.True(Has(caller, GscDiagnosticCode.DuplicateImport), "5018 reads the file");
        Assert.False(ReportsUnresolvedCall(calls), "5013/5014 need the index");
        Assert.False(Has(calls, GscDiagnosticCode.UnusedUsing), "5001 needs the import indexed");
    }

    [Fact]
    public async Task Off_AFolderAdded_IsNotIndexed()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Off);
        string added = Path.Combine(AddedFolder, "addon.gsc");
        workspace.Files.AddFile(added, LibSource);

        await FolderHandler(workspace, SettingsFor("off"), DiscardingDiagnosticsSink.Instance)
            .Handle(FolderChange(AddedFolder, removed: null), CancellationToken.None);

        Assert.False(workspace.Database.TryGetAnyRecord(added, out ScriptRecord _));
    }

    // --- partial -----------------------------------------------------------------------------

    [Fact]
    public async Task Partial_AnOpenFile_GetsTheCrossFileLints()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Partial);

        ImmutableArray<Diagnostic> caller = LintOpen(workspace, @"scripts\caller.gsc");
        ImmutableArray<Diagnostic> calls = LintOpen(workspace, @"scripts\calls.gsc");

        Assert.True(workspace.Database.HasCompletedIndex);
        Assert.True(Has(caller, GscDiagnosticCode.UsingNotFound));
        Assert.True(Has(caller, GscDiagnosticCode.DuplicateImport));
        Assert.True(ReportsUnresolvedCall(calls), "the index can say nosuch is nothing");
        Assert.True(Has(calls, GscDiagnosticCode.UnusedUsing), "the index can say lib contributes nothing");
    }

    [Fact]
    public async Task Partial_AClosedFile_ReportsParseDiagnosticsOnly()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Partial);

        Assert.True(workspace.Database.TryGetAnyRecord(TestPaths.Raw(@"scripts\calls.gsc"), out ScriptRecord record));
        Assert.False(ReportsUnresolvedCall(record.Diagnostics));
        Assert.False(Has(record.Diagnostics, GscDiagnosticCode.UnusedUsing));
    }

    [Fact]
    public async Task Partial_AFolderAdded_IsIndexedWithoutASweep_AndPublished()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Partial);
        string added = Path.Combine(AddedFolder, "addon.gsc");

        // A parse error, which a closed file reports in every mode, and a call to nothing, which in
        // partial mode it must not.
        workspace.Files.AddFile(added, "#namespace addon;\nfunction go()\n{\n\tx = ;\n\tnosuch();\n}\n");

        RecordingDiagnosticsSink sink = new();
        await FolderHandler(workspace, SettingsFor("partial"), sink)
            .Handle(FolderChange(AddedFolder, removed: null), CancellationToken.None);

        Assert.True(workspace.Database.TryGetAnyRecord(added, out ScriptRecord record));
        Assert.NotEmpty(record.Diagnostics);
        Assert.False(ReportsUnresolvedCall(record.Diagnostics), "no sweep in partial");

        // Published by the refresh the folder change now runs; before, nothing announced it.
        DocumentUri uri = DocumentUri.FromFileSystemPath(added);
        Assert.Contains(sink.Sent, sent => sent.Uri == uri && sent.Diagnostics.Any());
    }

    [Fact]
    public async Task AFolderRemoved_TakesBackItsProblems()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(Files, mode: IndexingMode.Partial);
        string added = Path.Combine(AddedFolder, "addon.gsc");
        workspace.Files.AddFile(added, "#namespace addon;\nfunction go()\n{\n\tx = ;\n}\n");

        RecordingDiagnosticsSink sink = new();
        WorkspaceFoldersHandler handler = FolderHandler(workspace, SettingsFor("partial"), sink);
        await handler.Handle(FolderChange(AddedFolder, removed: null), CancellationToken.None);
        await handler.Handle(FolderChange(added: null, AddedFolder), CancellationToken.None);

        DocumentUri uri = DocumentUri.FromFileSystemPath(added);
        PublishDiagnosticsParams last = sink.Sent.Last(sent => sent.Uri == uri);
        Assert.Empty(last.Diagnostics);
    }
}
