using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// One analysis in flight per document, and no analysis for a document nothing can reach.
///
/// The gate's coalescing was only ever exercised through the sync handler; the orphan check is new.
/// An analysis holds a direct reference to the OpenDocument it started on, so a close or a second
/// didOpen leaves it running against an object the store has already replaced — still publishing,
/// still committing, and able to overwrite the live document's fresher answer purely by finishing
/// after it.
/// </summary>
public class AnalysisGateTests
{
    private const string Source = "function main()\n{\n}\n";
    private static readonly string Path = TestPaths.Raw(@"scripts\main.gsc");

    private static DocumentStore NewStore()
    {
        return TestDocuments.Standalone();
    }

    [Fact]
    public void ASecondRequestWhileRunningQueuesExactlyOneRerun()
    {
        AnalysisGate gate = new();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryStart(CancellationToken.None));
        Assert.True(gate.TryContinue(out CancellationToken _));
        Assert.False(gate.TryContinue(out CancellationToken _));
    }

    [Fact]
    public void AThirdRequestDoesNotQueueASecondRerun()
    {
        // The point of the gate: a burst of keystrokes costs the run in flight plus ONE more,
        // against whatever text is current by the time that one starts.
        AnalysisGate gate = new();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryStart(CancellationToken.None));

        Assert.True(gate.TryContinue(out CancellationToken _));
        Assert.False(gate.TryContinue(out CancellationToken _));
    }

    [Fact]
    public void AFinishedGateLetsTheNextRunStart()
    {
        AnalysisGate gate = new();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryContinue(out CancellationToken _));

        Assert.True(gate.TryStart(CancellationToken.None));
    }

    [Fact]
    public void TakingAQueuedRerunKeepsTheGateHeld()
    {
        // The release and the queue check are one operation precisely so that a request landing
        // between them cannot be dropped. Taking a rerun must therefore NOT open the gate: if it
        // did, a third request could start a concurrent run beside the rerun about to happen.
        AnalysisGate gate = new();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryStart(CancellationToken.None));

        Assert.True(gate.TryContinue(out CancellationToken _));
        Assert.False(gate.TryStart(CancellationToken.None));
    }

    [Fact]
    public void AQueuedRerunCarriesTheQueueingRequestsToken()
    {
        // The running pass is normally CANCELLED by the very edit that queues the rerun, so a
        // rerun handed the running pass's token would be abandoned before it started. It runs
        // under the token of the edit that asked for it instead.
        AnalysisGate gate = new();

        using CancellationTokenSource running = new();
        using CancellationTokenSource queued = new();

        Assert.True(gate.TryStart(running.Token));
        Assert.False(gate.TryStart(queued.Token));

        running.Cancel();

        Assert.True(gate.TryContinue(out CancellationToken rerun));
        Assert.Equal(queued.Token, rerun);
        Assert.False(rerun.IsCancellationRequested);
    }

    [Fact]
    public void ReleaseAbandonsAQueuedRerun()
    {
        // Release is the path taken when the document is no longer the one the store holds, or
        // when something unexpected escaped the run. A rerun for a document nothing can reach is
        // not worth holding the gate for.
        AnalysisGate gate = new();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryStart(CancellationToken.None));

        gate.Release();

        Assert.True(gate.TryStart(CancellationToken.None));
        Assert.False(gate.TryContinue(out CancellationToken _));
    }

    [Fact]
    public void AnOpenDocumentIsLive()
    {
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, Source, version: 1);

        Assert.True(AnalysisGate.IsStillLive(store, document));
    }

    [Fact]
    public void AClosedDocumentIsNoLongerLive()
    {
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, Source, version: 1);

        store.Close(Path);

        Assert.False(AnalysisGate.IsStillLive(store, document));
    }

    [Fact]
    public void AReopenedDocumentOrphansTheFirstInstance()
    {
        // Same path, different object. Reference equality is what separates them — the orphan and
        // its replacement agree about everything else.
        DocumentStore store = NewStore();
        OpenDocument first = store.Open(Path, Source, version: 1);
        OpenDocument second = store.Open(Path, Source, version: 1);

        Assert.False(AnalysisGate.IsStillLive(store, first));
        Assert.True(AnalysisGate.IsStillLive(store, second));
    }
}
