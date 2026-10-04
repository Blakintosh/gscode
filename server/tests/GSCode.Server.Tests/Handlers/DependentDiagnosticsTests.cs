using GSCode.Core.Paths;
using GSCode.Server.Handlers;
using GSCode.Server.Configuration;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Which open documents get re-linted when an edit changes something they can see.
///
/// The cross-file lints read their neighbours, and nothing republished them: removing a
/// <c>#namespace</c> left every caller squiggle-free until each was reopened. Code lenses had the
/// same problem and solved it by asking the client to re-request; diagnostics are server-pushed,
/// so the server has to republish them itself.
/// </summary>
public class DependentDiagnosticsTests
{
    /// <summary>
    /// A document analysed at <paramref name="analyzedVersion"/> and then edited up to
    /// <paramref name="version"/>. Built through the store rather than by setting the two
    /// versions: the analysed version is published with its parse as one pair and has no setter,
    /// which is what stops the two from ever disagreeing about which text was analysed.
    /// </summary>
    private static OpenDocument Document(string path, int version = 1, int analyzedVersion = 1)
    {
        const string Source = "function main()\n{\n}\n";

        DocumentStore store = TestDocuments.Standalone();
        OpenDocument document = store.Open(path, Source, analyzedVersion);
        store.Analyze(document);

        if ( version != analyzedVersion )
        {
            store.ApplyChange(document, range: null, Source, version);
        }

        return document;
    }

    /// <summary>One origin, as the set ShouldRefresh's production overload actually takes.</summary>
    private static IReadOnlySet<string> OneOrigin(string path)
    {
        return new HashSet<string>(StringComparer.Ordinal) { path };
    }

    [Fact]
    public void ANeighbourIsRefreshed()
    {
        // The whole point: another open file's diagnostics were computed against the edited one.
        Assert.True(DependentDiagnosticsRefresher.ShouldRefresh(
            Document(TestPaths.Raw(@"caller.gsc")), OneOrigin(TestPaths.Raw(@"util.gsc"))));
    }

    [Fact]
    public void TheEditedDocumentIsNot()
    {
        // Its own handler is publishing it; doing it here as well would only race that.
        Assert.False(DependentDiagnosticsRefresher.ShouldRefresh(
            Document(TestPaths.Raw(@"util.gsc")), OneOrigin(TestPaths.Raw(@"util.gsc"))));
    }

    [Fact]
    public void ADocumentMidEditIsNot()
    {
        // Text newer than anything committed, and a debounced analysis of its own already queued.
        // Publishing here would describe text the user has already replaced.
        OpenDocument typing = Document(TestPaths.Raw(@"caller.gsc"), version: 7, analyzedVersion: 4);

        Assert.True(typing.IsStale);
        Assert.False(DependentDiagnosticsRefresher.ShouldRefresh(typing, OneOrigin(TestPaths.Raw(@"util.gsc"))));
    }

    // --- ClosedDependentsOf (F7: the full-mode closed-file half) ---

    private const string LibRelativePath = @"scripts\lib.gsc";
    private const string CallerRelativePath = @"scripts\caller.gsc";

    private static readonly string s_libPath = TestPaths.Raw(LibRelativePath);
    private static readonly string s_callerPath = TestPaths.Raw(CallerRelativePath);

    /// <summary>lib declares helper; caller imports lib and calls it. Neither is open.</summary>
    private static Task<HandlerWorkspace> TwoFileWorkspaceAsync()
    {
        return HandlerWorkspace.BuildAsync(
        [
            new TestFile(LibRelativePath, "#namespace lib;\nfunction helper()\n{\n}\n"),
            new TestFile(CallerRelativePath, "#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n    lib::helper();\n}\n"),
        ]);
    }

    private static ScriptRecord OriginIn(HandlerWorkspace workspace)
    {
        Assert.True(workspace.Database.TryGetAnyRecord(s_libPath, out ScriptRecord origin));
        return origin;
    }

    [Fact]
    public async Task ClosedDependentsOf_FindsAClosedCaller()
    {
        using HandlerWorkspace workspace = await TwoFileWorkspaceAsync();

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(
            OriginIn(workspace), workspace.Database.Gsc, workspace.Documents);

        Assert.Contains(PathUtil.NormalizeAbsolute(s_callerPath), dependents);
    }

    [Fact]
    public async Task ClosedDependentsOf_ExcludesAnOpenCaller()
    {
        // The live-analysis path already covers an open file with the richer, real-time result —
        // re-linting it here from disk would describe whatever was last SAVED instead.
        using HandlerWorkspace workspace = await TwoFileWorkspaceAsync();
        workspace.Documents.Open(s_callerPath, "irrelevant buffer text", version: 1);

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(
            OriginIn(workspace), workspace.Database.Gsc, workspace.Documents);

        Assert.Empty(dependents);
    }

    [Fact]
    public async Task ClosedDependentsOf_ExcludesTheOriginItself()
    {
        using HandlerWorkspace workspace = await TwoFileWorkspaceAsync();

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(
            OriginIn(workspace), workspace.Database.Gsc, workspace.Documents);

        Assert.DoesNotContain(PathUtil.NormalizeAbsolute(s_libPath), dependents);
    }

    private sealed class CountingCodeLensSink : ICodeLensRefreshSink
    {
        public int Requests { get; private set; }

        public void Request()
        {
            Requests++;
        }
    }

    /// <summary>
    /// A refresher over an empty workspace. Every collaborator is a real but empty instance, which
    /// is all the queueing question needs: the pass is cancelled before it reaches any of them.
    /// </summary>
    private static DependentDiagnosticsRefresher EmptyRefresher(HandlerWorkspace workspace)
    {
        return EmptyRefresher(workspace, NullCodeLensRefreshSink.Instance, new ServerSettings());
    }

    private static DependentDiagnosticsRefresher EmptyRefresher(
        HandlerWorkspace workspace, ICodeLensRefreshSink codeLenses, ServerSettings settings)
    {
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);

        DiagnosticsPublisher publisher = new(DiscardingDiagnosticsSink.Instance);
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(
            workspace.Database, workspace.Documents, publisher, new ServerSettings());
        WorkspaceLintSweep sweep = new(workspace.Database, workspace.Documents, workspace.Indexer, linter);

        return new DependentDiagnosticsRefresher(
            workspace.Documents, publisher, linter, workspace.Database, sweep, workspaceDiagnostics, codeLenses, settings);
    }

    [Fact]
    public async Task AnOriginAbandonedAfterTheTakeGoesBackInTheQueue()
    {
        // The set is cleared the moment a pass takes it, so a pass cancelled AFTER that point used
        // to drop every origin it had not finished with. Nothing else re-lints a closed dependent,
        // so those files kept diagnostics computed against exports the origin no longer has until
        // some unrelated later edit happened to name the same file.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DependentDiagnosticsRefresher refresher = EmptyRefresher(workspace);
        refresher.Schedule(TestPaths.Raw(@"util.gsc"));

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresher.RunPassAsync(cancelled.Token));

        Assert.Contains(TestPaths.Raw(@"util.gsc"), refresher.PendingOrigins);
    }

    [Fact]
    public async Task AFinishedPassLeavesTheQueueEmpty()
    {
        // The control: an uncancelled pass over an empty workspace consumes its origins rather than
        // handing them back, so the case above cannot pass by nothing ever being taken.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DependentDiagnosticsRefresher refresher = EmptyRefresher(workspace);
        refresher.Schedule(TestPaths.Raw(@"util.gsc"));

        await refresher.RunPassAsync(CancellationToken.None);

        Assert.Empty(refresher.PendingOrigins);
    }

    [Fact]
    public async Task ThreeOriginsInOneFanOutAskForOneCodeLensRefresh()
    {
        // It used to be sent from TextSyncHandler per ANALYSIS and undebounced. Typing a function's
        // name moves the export signature on every keystroke, so a client with lenses on
        // re-requested them for every visible document about four times a second — and one such
        // request measured 164 ms on the densest cod4 script.
        CountingCodeLensSink lenses = new();
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DependentDiagnosticsRefresher refresher = EmptyRefresher(workspace, lenses, new ServerSettings { CodeLensEnabled = true });

        refresher.Schedule(TestPaths.Raw(@"a.gsc"));
        refresher.Schedule(TestPaths.Raw(@"b.gsc"));
        refresher.Schedule(TestPaths.Raw(@"c.gsc"));

        await refresher.RunPassAsync(CancellationToken.None);

        Assert.Equal(1, lenses.Requests);
    }

    [Fact]
    public async Task NoRefreshIsAskedForWhenLensesAreOff()
    {
        // codeLens.enabled is off by default, and a client that shows no lenses has none to
        // re-request.
        CountingCodeLensSink lenses = new();
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DependentDiagnosticsRefresher refresher = EmptyRefresher(workspace, lenses, new ServerSettings { CodeLensEnabled = false });

        refresher.Schedule(TestPaths.Raw(@"a.gsc"));

        await refresher.RunPassAsync(CancellationToken.None);

        Assert.Equal(0, lenses.Requests);
    }
}
