using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Two <c>didOpen</c>s for the same path with no <c>didClose</c> between them — a client re-sending
/// one on tab focus, or a race during a restored session (<see cref="DocumentStore.Open"/>'s own
/// comment) — reproduced without any of <see cref="GSCode.Server.Handlers.TextSyncHandler"/>'s other
/// dependencies, since the bug lives entirely in the coordination between
/// <see cref="AnalysisGate"/> and a document being REPLACED mid-analysis.
///
/// The reported symptom: rarely, opening a document while startup indexing was still running left
/// its symbols/outline never populating, even though the file's OWN analysis eventually ran for
/// some other trigger. A slow, cold index is exactly what widens the window between one didOpen's
/// analysis starting and a second one landing before the first finishes.
/// </summary>
public class SingleFlightAnalysisTests
{
    private static readonly string Path = @"c:\bo3\share\raw\scripts\main.gsc";

    private static DocumentStore NewStore()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }

    [Fact]
    public void ASecondDidOpen_DuringTheFirstsAnalysis_StillGetsAnalysed()
    {
        DocumentStore documents = NewStore();
        SingleFlightAnalysis singleFlight = new(documents);

        OpenDocument first = documents.Open(Path, "function one(){}\n", version: 1);
        using CancellationTokenSource firstCts = new();
        first.PendingAnalysis = firstCts;

        // ONE delegate for every pass, matching production exactly: TextSyncHandler always passes
        // the same AnalyzeAndPublish method reference to Run, whichever document it is called
        // with — the delegate is the fixed OPERATION, the document argument is what varies.
        List<string> analysedText = [];
        bool secondOpened = false;

        void Analyze(OpenDocument document, CancellationToken token)
        {
            analysedText.Add(document.Text.Text);

            if ( !secondOpened )
            {
                secondOpened = true;

                // The second didOpen, landing WHILE the first document's analysis is still
                // running — the exact interleaving DocumentStore.Open's own comment describes.
                // Open() cancels `first`'s PendingAnalysis on its way past, which is what makes
                // this pass's own token observe the cancellation below, the same way
                // TextSyncHandler.ScheduleImmediateAnalysis wires PendingAnalysis to the token
                // AnalyzeAndPublish actually runs under.
                OpenDocument second = documents.Open(Path, "function two(){}\n", version: 2);
                using CancellationTokenSource secondCts = new();
                second.PendingAnalysis = secondCts;
                singleFlight.Run(second, secondCts.Token, Analyze);

                token.ThrowIfCancellationRequested();
            }
        }

        singleFlight.Run(first, firstCts.Token, Analyze);

        Assert.Contains("function two(){}\n", analysedText);
    }

    [Fact]
    public void WithNoReplacement_ASingleDocumentIsAnalysedOnce()
    {
        // The ordinary case this must not disturb: one document, one analysis.
        DocumentStore documents = NewStore();
        SingleFlightAnalysis singleFlight = new(documents);

        OpenDocument document = documents.Open(Path, "function f(){}\n", version: 1);
        int calls = 0;

        singleFlight.Run(document, CancellationToken.None, (_, _) => calls++);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ASecondRunWhileTheFirstIsInFlight_IsCoalescedRatherThanRunTwice()
    {
        // A rerun queued for the SAME document (an ordinary debounced edit, not a replacement)
        // still runs exactly once more, not concurrently with the first pass.
        DocumentStore documents = NewStore();
        SingleFlightAnalysis singleFlight = new(documents);

        OpenDocument document = documents.Open(Path, "function f(){}\n", version: 1);
        List<int> order = [];

        void Analyze(OpenDocument doc, CancellationToken token)
        {
            order.Add(order.Count);
            if ( order.Count == 1 )
            {
                // A rerun request arriving while this (the only) pass is running.
                singleFlight.Run(document, CancellationToken.None, Analyze);
            }
        }

        singleFlight.Run(document, CancellationToken.None, Analyze);

        Assert.Equal(2, order.Count);
    }
}
