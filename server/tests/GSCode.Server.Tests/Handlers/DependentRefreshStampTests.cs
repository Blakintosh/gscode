using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
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
public sealed class DependentRefreshStampTests
{
    private const string Source = "function main()\n{\n}\n";

    /// <summary>How long a gate may wait before the test is declared hung rather than slow.</summary>
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// A document store whose SECOND analysis parks inside the insert-provider factory — the one
    /// place a test can hold an analysis open while an edit lands on the same document. The first
    /// analysis is the document's own, which has to complete for the refresh to have anything to be
    /// stale against. This store is the subject, so it is built here rather than taken from the
    /// harness; everything else the refresher needs is the harness's.
    /// </summary>
    private static DocumentStore GatedDocuments(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        int started = 0;

        return new DocumentStore(
            _ =>
            {
                if ( Interlocked.Increment(ref started) == 2 )
                {
                    entered.Set();
                    release.Wait(s_patience);
                }

                return NullInsertProvider.Instance;
            },
            new NameTable());
    }

    private static DependentDiagnosticsRefresher RefresherOver(
        HandlerWorkspace workspace, DocumentStore documents, RecordingSink sink)
    {
        DocumentLinter linter = new(
            workspace.Database, workspace.ResolverHolder, workspace.Builtins, workspace.ObjectFields);

        DiagnosticsPublisher publisher = new(sink);
        WorkspaceDiagnosticsPublisher workspaceDiagnostics = new(
            workspace.Database, documents, publisher, new ServerSettings());
        WorkspaceLintSweep sweep = new(workspace.Database, documents, workspace.Indexer, linter);

        return new DependentDiagnosticsRefresher(
            documents, publisher, linter, workspace.Database, sweep, workspaceDiagnostics,
            NullCodeLensRefreshSink.Instance, new ServerSettings());
    }

    [Fact]
    public async Task RefreshOneStampsTheWinningSnapshotsVersion_NotTheLiveOne()
    {
        RecordingSink sink = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DocumentStore documents = GatedDocuments(entered, release);
        DependentDiagnosticsRefresher refresher = RefresherOver(workspace, documents, sink);

        OpenDocument document = documents.Open(TestPaths.Raw(@"scripts\caller.gsc"), Source, version: 1);
        documents.Analyze(document);

        // Stale, so the refresh has to re-analyse rather than reuse the cached parse.
        documents.ApplyChange(document, range: null, Source, version: 2);

        Task refresh = Task.Run(() => refresher.RefreshOne(document));
        Assert.True(entered.Wait(s_patience), "the refresh never reached the analysis");

        // The edit that used to win the stamp: the parse in flight is of v2's text.
        documents.ApplyChange(document, range: null, Source, version: 7);
        release.Set();
        await refresh.WaitAsync(s_patience);

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

        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync([]);
        DocumentStore documents = GatedDocuments(entered, release);
        DependentDiagnosticsRefresher refresher = RefresherOver(workspace, documents, sink);

        OpenDocument first = documents.Open(TestPaths.Raw(@"scripts\first.gsc"), Source, version: 1);
        OpenDocument second = documents.Open(TestPaths.Raw(@"scripts\second.gsc"), Source, version: 1);
        OpenDocument bystander = documents.Open(TestPaths.Raw(@"scripts\bystander.gsc"), Source, version: 1);
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
