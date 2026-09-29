using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Api;
using GSCode.Server.Configuration;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
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

        DocumentStore store = new(static _ => NullInsertProvider.Instance, new NameTable());
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

    private static readonly string LibPath = TestPaths.Raw(@"lib.gsc");
    private static readonly string CallerPath = TestPaths.Raw(@"caller.gsc");

    private static (ScriptDatabase Database, ScriptRecord Origin) BuildTwoFileWorkspace()
    {
        NameTable names = new();
        ScriptDatabase database = new();

        ParseResult lib = ScriptAnalysis.Analyze(
            LibPath, ScriptLanguage.Gsc, SourceText.From("#namespace lib;\nfunction helper()\n{\n}\n"),
            NullInsertProvider.Instance, names);
        ScriptRecord origin = database.Commit(lib, ResolutionContext.RawContext, isDirty: false, "lib.gsc");

        ParseResult caller = ScriptAnalysis.Analyze(
            CallerPath, ScriptLanguage.Gsc,
            SourceText.From("#using scripts\\lib;\n#namespace game;\nfunction run()\n{\n    lib::helper();\n}\n"),
            NullInsertProvider.Instance, names);
        database.Commit(caller, ResolutionContext.RawContext, isDirty: false, "caller.gsc");

        return (database, origin);
    }

    [Fact]
    public void ClosedDependentsOf_FindsAClosedCaller()
    {
        (ScriptDatabase database, ScriptRecord origin) = BuildTwoFileWorkspace();
        DocumentStore noOpenDocuments = new(static _ => NullInsertProvider.Instance, new NameTable());

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(origin, database.Gsc, noOpenDocuments);

        Assert.Contains(PathUtil.NormalizeAbsolute(CallerPath), dependents);
    }

    [Fact]
    public void ClosedDependentsOf_ExcludesAnOpenCaller()
    {
        // The live-analysis path already covers an open file with the richer, real-time result —
        // re-linting it here from disk would describe whatever was last SAVED instead.
        (ScriptDatabase database, ScriptRecord origin) = BuildTwoFileWorkspace();
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        documents.Open(CallerPath, "irrelevant buffer text", version: 1);

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(origin, database.Gsc, documents);

        Assert.Empty(dependents);
    }

    [Fact]
    public void ClosedDependentsOf_ExcludesTheOriginItself()
    {
        (ScriptDatabase database, ScriptRecord origin) = BuildTwoFileWorkspace();
        DocumentStore noOpenDocuments = new(static _ => NullInsertProvider.Instance, new NameTable());

        HashSet<string> dependents = DependentDiagnosticsRefresher.ClosedDependentsOf(origin, database.Gsc, noOpenDocuments);

        Assert.DoesNotContain(PathUtil.NormalizeAbsolute(LibPath), dependents);
    }

    private sealed class DiscardingSink : IDiagnosticsSink
    {
        public void Send(PublishDiagnosticsParams parameters)
        {
        }
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
    private static DependentDiagnosticsRefresher EmptyRefresher()
    {
        return EmptyRefresher(NullCodeLensRefreshSink.Instance, new ServerSettings());
    }

    private static DependentDiagnosticsRefresher EmptyRefresher(ICodeLensRefreshSink codeLenses, ServerSettings settings)
    {
        PhysicalFileSystem fileSystem = new();
        ResolverHolder resolverHolder = new(fileSystem);
        NameTable names = new();
        ScriptDatabase database = new();
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, names);
        WorkspaceIndexer indexer = new(database, () => resolverHolder.Current, fileSystem, names);

        string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
        DocumentLinter linter = new(
            database, resolverHolder, BuiltinApiSet.Load(apiDirectory), ObjectFields.Load(apiDirectory));

        DiagnosticsPublisher publisher = new(new DiscardingSink());
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(database, documents, publisher, new ServerSettings());
        WorkspaceLintSweep sweep = new(database, documents, indexer, linter);

        return new DependentDiagnosticsRefresher(
            documents, publisher, linter, database, sweep, workspaceDiagnostics, codeLenses, settings);
    }

    [Fact]
    public async Task AnOriginAbandonedAfterTheTakeGoesBackInTheQueue()
    {
        // The set is cleared the moment a pass takes it, so a pass cancelled AFTER that point used
        // to drop every origin it had not finished with. Nothing else re-lints a closed dependent,
        // so those files kept diagnostics computed against exports the origin no longer has until
        // some unrelated later edit happened to name the same file.
        DependentDiagnosticsRefresher refresher = EmptyRefresher();
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
        DependentDiagnosticsRefresher refresher = EmptyRefresher();
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
        DependentDiagnosticsRefresher refresher = EmptyRefresher(lenses, new ServerSettings { CodeLensEnabled = true });

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
        DependentDiagnosticsRefresher refresher = EmptyRefresher(lenses, new ServerSettings { CodeLensEnabled = false });

        refresher.Schedule(TestPaths.Raw(@"a.gsc"));

        await refresher.RunPassAsync(CancellationToken.None);

        Assert.Equal(0, lenses.Requests);
    }
}
