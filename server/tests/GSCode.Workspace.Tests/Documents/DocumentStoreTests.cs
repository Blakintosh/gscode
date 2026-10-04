using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Workspace.Tests.Documents;

/// <summary>
/// The reported bug: a second <c>didOpen</c> for a path already open — a client re-sending one
/// on tab focus, or a race during a restored session — replaced the entry in <c>_documents</c>
/// outright, but the FIRST open's own immediate analysis (scheduled by the sync handler) held a
/// direct reference to that now-unreachable OLD <see cref="OpenDocument"/>, not to whatever a
/// lookup would find. Nothing cancelled it, so it ran to completion as an orphan able to publish
/// diagnostics and commit a record from stale text purely by finishing after the second open's
/// own analysis — the same failure mode <see cref="DocumentStore.Close"/> already guards against
/// for a document that stops being open outright.
/// </summary>
public class DocumentStoreTests
{
    private static DocumentStore Build()
    {
        return TestDocuments.Standalone();
    }

    [Fact]
    public void Open_CancelsThePreviousDocumentsPendingAnalysis()
    {
        DocumentStore store = Build();
        OpenDocument first = store.Open(TestPaths.Raw(@"scripts\t.gsc"), "function f()\n{\n}\n", version: 1);

        // Scheduled the way TextSyncHandler.ScheduleImmediateAnalysis does for the first open,
        // standing in for the orphan's in-flight token.
        CancellationTokenSource pending = new();
        first.PendingAnalysis = pending;

        store.Open(TestPaths.Raw(@"scripts\t.gsc"), "function f()\n{\n}\n", version: 1);

        Assert.True(pending.IsCancellationRequested);
    }

    [Fact]
    public void Open_WithNoPriorAnalysisPending_DoesNotThrow()
    {
        DocumentStore store = Build();
        store.Open(TestPaths.Raw(@"scripts\t.gsc"), "function f()\n{\n}\n", version: 1);

        OpenDocument reopened = store.Open(TestPaths.Raw(@"scripts\t.gsc"), "function f()\n{\n}\n", version: 2);

        Assert.Equal(2, reopened.Version);
    }

    [Fact]
    public void Open_ForADifferentPath_DoesNotCancelAnUnrelatedDocument()
    {
        DocumentStore store = Build();
        OpenDocument other = store.Open(TestPaths.Raw(@"scripts\other.gsc"), "function g()\n{\n}\n", version: 1);

        CancellationTokenSource pending = new();
        other.PendingAnalysis = pending;

        store.Open(TestPaths.Raw(@"scripts\t.gsc"), "function f()\n{\n}\n", version: 1);

        Assert.False(pending.IsCancellationRequested);
    }
}
