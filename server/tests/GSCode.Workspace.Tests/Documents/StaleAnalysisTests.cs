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
    public void AnEditArrivingDuringAnalysisLeavesTheDocumentStale()
    {
        // Analyze stamps the version it READ, not the version at the end. Stamping the latter
        // would mark the document current against text that was never analysed — the same bug,
        // reintroduced through the fix.
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(@"C:\bo3\share\raw\scripts\main.gsc", "#p\n", version: 1);

        // Stand in for the race: the edit lands between Analyze reading Version and finishing.
        document.Version = 2;
        document.Text = SourceText.From("#pre\n");
        document.AnalyzedVersion = 1;

        Assert.True(document.IsStale);
    }
}
