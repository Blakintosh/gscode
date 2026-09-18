using GSCode.Core;
using GSCode.Parser.Preprocessing;
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
    private const string Path = @"C:\bo3\share\raw\scripts\main.gsc";

    private static DocumentStore NewStore()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }

    [Fact]
    public void ASecondRequestWhileRunningQueuesExactlyOneRerun()
    {
        AnalysisGate gate = new();

        Assert.True(gate.TryStart());
        Assert.False(gate.TryStart());
        Assert.True(gate.ConsumeRerunRequest());
        Assert.False(gate.ConsumeRerunRequest());
    }

    [Fact]
    public void AThirdRequestDoesNotQueueASecondRerun()
    {
        // The point of the gate: a burst of keystrokes costs the run in flight plus ONE more,
        // against whatever text is current by the time that one starts.
        AnalysisGate gate = new();

        Assert.True(gate.TryStart());
        Assert.False(gate.TryStart());
        Assert.False(gate.TryStart());

        Assert.True(gate.ConsumeRerunRequest());
        Assert.False(gate.ConsumeRerunRequest());
    }

    [Fact]
    public void AFinishedGateLetsTheNextRunStart()
    {
        AnalysisGate gate = new();

        Assert.True(gate.TryStart());
        gate.Finish();

        Assert.True(gate.TryStart());
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
