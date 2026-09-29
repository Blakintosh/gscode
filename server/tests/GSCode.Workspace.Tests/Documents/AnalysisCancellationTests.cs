using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Workspace.Tests.Documents;

/// <summary>
/// A superseded analysis stops instead of finishing and publishing.
///
/// The debounce bounds when a pass STARTS. Nothing bounded how long one runs: the token reached
/// <c>Task.Delay</c> and no further, so every pass the user had already typed past ran to
/// completion — holding its own token arrays and tree — and then published. On a file slow enough
/// to outrun the 250 ms debounce that is several dead analyses in flight at once, which is the
/// memory face of the same bug.
/// </summary>
public class AnalysisCancellationTests
{
    private const string Source = "function main()\n{\n    x = 1;\n}\n";

    /// <summary>How long a gate may wait before the test is declared hung rather than slow.</summary>
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A store whose first analysis parks inside the insert-provider factory — the one place a test
    /// can hold an analysis open while something else happens to the same document.
    /// </summary>
    private static DocumentStore GatedStore(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        int started = 0;

        return new DocumentStore(
            _ =>
            {
                if ( Interlocked.Increment(ref started) == 1 )
                {
                    entered.Set();
                    release.Wait(s_patience);
                }

                return NullInsertProvider.Instance;
            },
            new NameTable());
    }

    [Fact]
    public async Task ACancelledAnalysisStopsInsteadOfPublishing()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        using CancellationTokenSource cancellation = new();

        DocumentStore store = GatedStore(entered, release);
        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", Source, version: 1);

        Task analysis = Task.Run(() => store.AnalyzeSnapshot(document, cancellation.Token));
        Assert.True(entered.Wait(s_patience), "the analysis never started");

        cancellation.Cancel();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);

        // Nothing was published, so the document is still reported as never analysed rather than
        // holding a parse of text that has been superseded.
        Assert.Null(document.Analysis);
    }

    [Fact]
    public async Task AnUncancelledAnalysisStillPublishes()
    {
        // The control. Without it the test above could pass by never analysing at all.
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();

        DocumentStore store = GatedStore(entered, release);
        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", Source, version: 1);

        Task analysis = Task.Run(() => store.AnalyzeSnapshot(document, CancellationToken.None));
        Assert.True(entered.Wait(s_patience), "the analysis never started");

        release.Set();
        await analysis.WaitAsync(s_patience);

        Assert.Equal(1, document.Analysis?.Version);
    }
}
