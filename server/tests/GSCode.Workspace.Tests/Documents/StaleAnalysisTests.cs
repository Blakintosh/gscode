using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Workspace.Tests.Documents;

/// <summary>
/// Text updates on every keystroke; analysis is debounced by 250 ms. Anything that pairs a LIVE
/// cursor position with the STALE result text is therefore reading characters the user has
/// already replaced.
///
/// That is not hypothetical — it is why directive completion "sometimes worked": typing `#p`
/// slowly enough for the debounce to fire offered `#precache`, while typing on to `#pre` in one
/// burst offered `private`, because the analysed text still ended at `#p` (or earlier) and the
/// '#'-context check looked at the wrong characters. Every unit test passed throughout, since
/// they all analyse synchronously and never desynchronise the two.
/// </summary>
public class StaleAnalysisTests
{
    private static DocumentStore NewStore()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>How long a gate may wait before the test is declared hung rather than slow.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A store whose FIRST analysis parks inside the insert-provider factory. The factory is
    /// called within Analyze, which makes it the one place a test can hold an analysis open while
    /// an edit — or a second analysis — lands on the same document.
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
                    release.Wait(Patience);
                }

                return NullInsertProvider.Instance;
            },
            new NameTable());
    }

    private static OpenDocument OpenAndAnalyze(DocumentStore store, string text)
    {
        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", text, version: 1);
        store.Analyze(document);
        return document;
    }

    [Fact]
    public void AFreshlyAnalysedDocumentIsNotStale()
    {
        DocumentStore store = NewStore();

        Assert.False(OpenAndAnalyze(store, "function f()\n{\n}\n").IsStale);
    }

    [Fact]
    public void AnEditWithoutAnalysisMakesItStale()
    {
        DocumentStore store = NewStore();
        OpenDocument document = OpenAndAnalyze(store, "function f()\n{\n}\n");

        store.ApplyChange(document, range: null, "#p\nfunction f()\n{\n}\n", version: 2);

        Assert.True(document.IsStale);
    }

    [Fact]
    public void TheStaleResultStillHoldsTheOldText()
    {
        // The mechanism itself: the result the editor would have answered from describes text
        // that is no longer on screen.
        DocumentStore store = NewStore();
        OpenDocument document = OpenAndAnalyze(store, "#p\n");

        store.ApplyChange(document, range: null, "#pre\n", version: 2);

        Assert.Equal("#pre\n", document.Text.Text);
        Assert.Equal("#p\n", document.LatestResult!.Text.Text);
    }

    [Fact]
    public void AnalyzeIfStale_ReanalysesAndMatchesTheLiveText()
    {
        DocumentStore store = NewStore();
        OpenDocument document = OpenAndAnalyze(store, "#p\n");
        store.ApplyChange(document, range: null, "#pre\n", version: 2);

        ParseResult result = store.AnalyzeIfStale(document);

        Assert.Equal(document.Text.Text, result.Text.Text);
        Assert.False(document.IsStale);
    }

    [Fact]
    public void AnalyzeIfStale_ReusesTheResultWhenNothingChanged()
    {
        // Completion runs this on every keystroke, so an unchanged document must not pay for a
        // second analysis.
        DocumentStore store = NewStore();
        OpenDocument document = OpenAndAnalyze(store, "function f()\n{\n}\n");
        ParseResult first = document.LatestResult!;

        Assert.Same(first, store.AnalyzeIfStale(document));
    }

    [Fact]
    public async Task AnOlderAnalysisFinishingLast_DoesNotReplaceTheNewerOne()
    {
        // Two analyses run on one document at once: the debounced one on a thread-pool
        // continuation while a request thread enters AnalyzeIfStale through ResolveFresh. They do
        // not finish in the order they started, and the one that finishes last used to win — so
        // the document could end up holding a parse of text the user replaced two edits ago, or
        // (writing the result and the version separately) a NEW version stamped on an OLD parse,
        // which reports itself fresh.
        //
        ManualResetEventSlim insideFirst = new(false);
        ManualResetEventSlim releaseFirst = new(false);
        DocumentStore store = GatedStore(insideFirst, releaseFirst);

        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", "#p\n", version: 1);

        Task<ParseResult> older = Task.Run(() => store.Analyze(document));
        Assert.True(insideFirst.Wait(Patience));

        store.ApplyChange(document, range: null, "#pre\n", version: 2);
        store.Analyze(document);

        releaseFirst.Set();
        ParseResult olderResult = await older.WaitAsync(Patience);

        Assert.Equal(2, document.AnalyzedVersion);
        Assert.Equal("#pre\n", document.LatestResult!.Text.Text);
        Assert.False(document.IsStale);

        // The superseded analysis' own caller publishes diagnostics from what Analyze hands back,
        // so it has to be handed the parse that won, not the one it computed.
        Assert.Equal("#pre\n", olderResult.Text.Text);
    }

    [Fact]
    public async Task AnEditArrivingDuringAnalysisLeavesTheDocumentStale()
    {
        // Analyze stamps the version it READ, not the version at the end. Stamping the latter
        // would mark the document current against text that was never analysed — the same bug,
        // reintroduced through the fix.
        ManualResetEventSlim inside = new(false);
        ManualResetEventSlim release = new(false);
        DocumentStore store = GatedStore(inside, release);

        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", "#p\n", version: 1);

        Task<ParseResult> analysis = Task.Run(() => store.Analyze(document));
        Assert.True(inside.Wait(Patience));

        // The edit lands after Analyze has taken the version and the text, and before it finishes.
        store.ApplyChange(document, range: null, "#pre\n", version: 2);

        release.Set();
        await analysis.WaitAsync(Patience);

        Assert.True(document.IsStale);
        Assert.Equal(1, document.AnalyzedVersion);
        Assert.Equal("#p\n", document.LatestResult!.Text.Text);
    }
}
