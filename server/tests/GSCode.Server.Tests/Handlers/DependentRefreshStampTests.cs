using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A cross-file refresh publishes the version it ANALYSED, not whatever the document has become
/// by the time the publish happens.
///
/// The sync handler was fixed for exactly this (see
/// <c>StaleAnalysisTests.AnalyzeSnapshot_OnTheOlderCaller_ReportsTheWinnersVersion_NotItsOwn</c>)
/// and the fan-out kept the old shape: analyse, then read <c>document.Version</c>. An edit landing
/// in between stamps a parse of older text with the newest version — the one stamp a client uses to
/// decide a set is still current.
/// </summary>
public sealed class DependentRefreshStampTests : IDisposable
{
    private const string Source = "function main()\n{\n}\n";

    /// <summary>How long a gate may wait before the test is declared hung rather than slow.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private sealed class RecordingSink : IDiagnosticsSink
    {
        private readonly object _gate = new();

        public List<PublishDiagnosticsParams> Sent { get; } = [];

        public void Send(PublishDiagnosticsParams parameters)
        {
            lock ( _gate )
            {
                Sent.Add(parameters);
            }
        }
    }

    private readonly string _root;

    public DependentRefreshStampTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-refresh-stamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch ( IOException )
        {
            // Best-effort cleanup; a locked file left behind costs nothing a later run cannot fix.
        }
    }

    /// <summary>
    /// A refresher over a document store whose SECOND analysis parks inside the insert-provider
    /// factory — the one place a test can hold an analysis open while an edit lands on the same
    /// document. The first analysis is the document's own, which has to complete for the refresh to
    /// have anything to be stale against.
    /// </summary>
    private DependentDiagnosticsRefresher Build(
        RecordingSink sink, ManualResetEventSlim entered, ManualResetEventSlim release, out DocumentStore documents)
    {
        int started = 0;

        documents = new DocumentStore(
            _ =>
            {
                if ( Interlocked.Increment(ref started) == 2 )
                {
                    entered.Set();
                    release.Wait(Patience);
                }

                return NullInsertProvider.Instance;
            },
            new NameTable());

        PhysicalFileSystem fileSystem = new();
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: _root, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);
        PathResolver resolver = new(config, fileSystem);
        ResolverHolder resolverHolder = new(fileSystem) { Current = resolver };

        NameTable names = new();
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
        DocumentLinter linter = new(
            database,
            resolverHolder,
            BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api")),
            ObjectFields.Load(Path.Combine(AppContext.BaseDirectory, "Api")));

        DiagnosticsPublisher publisher = new(sink);
        ServerSettings settings = new();
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(database, documents, publisher, settings);
        WorkspaceLintSweep sweep = new(database, documents, indexer, linter);

        return new DependentDiagnosticsRefresher(
            documents, publisher, linter, database, sweep, workspaceDiagnostics);
    }

    [Fact]
    public async Task RefreshOneStampsTheWinningSnapshotsVersion_NotTheLiveOne()
    {
        RecordingSink sink = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        DependentDiagnosticsRefresher refresher = Build(sink, entered, release, out DocumentStore documents);

        string path = Path.Combine(_root, "caller.gsc");
        OpenDocument document = documents.Open(path, Source, version: 1);
        documents.Analyze(document);

        // Stale, so the refresh has to re-analyse rather than reuse the cached parse.
        documents.ApplyChange(document, range: null, Source, version: 2);

        Task refresh = Task.Run(() => refresher.RefreshOne(document));
        Assert.True(entered.Wait(Patience), "the refresh never reached the analysis");

        // The edit that used to win the stamp: the parse in flight is of v2's text.
        documents.ApplyChange(document, range: null, Source, version: 7);
        release.Set();
        await refresh.WaitAsync(Patience);

        PublishDiagnosticsParams published = Assert.Single(sink.Sent);
        Assert.Equal(2, published.Version);
    }

    [Fact]
    public async Task ARefreshForOneFileIsNotCancelledByAScheduleForAnother()
    {
        // One token source served every origin, so scheduling B cancelled A's pass outright and
        // A's dependents were never refreshed — the callers of the file edited first kept
        // diagnostics computed against exports it no longer has.
        RecordingSink sink = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        release.Set();

        DependentDiagnosticsRefresher refresher = Build(sink, entered, release, out DocumentStore documents);

        OpenDocument first = documents.Open(Path.Combine(_root, "first.gsc"), Source, version: 1);
        OpenDocument second = documents.Open(Path.Combine(_root, "second.gsc"), Source, version: 1);
        OpenDocument bystander = documents.Open(Path.Combine(_root, "bystander.gsc"), Source, version: 1);
        documents.Analyze(first);
        documents.Analyze(second);
        documents.Analyze(bystander);

        refresher.Schedule(first.Path);
        refresher.Schedule(second.Path);

        // Long enough for the 900 ms fan-out debounce, with room for a slow machine.
        await Task.Delay(TimeSpan.FromSeconds(2.5));

        // The bystander once, and neither origin: both are excluded, not just the last one queued.
        PublishDiagnosticsParams published = Assert.Single(sink.Sent);
        Assert.Equal(bystander.Path, published.Uri.GetFileSystemPath());
    }
}
