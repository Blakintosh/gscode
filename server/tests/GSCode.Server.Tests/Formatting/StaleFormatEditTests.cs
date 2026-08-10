using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using GSCode.Workspace.Documents;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A formatting edit is only valid against the text it was computed from.
///
/// FormatMinimal trims the common prefix and suffix so the edit spans only what changed, which
/// makes its range a set of offsets INTO that text. Analysis is debounced 250 ms behind the
/// keystrokes, so a format arriving in that window (format-on-save fires right after edits, and
/// on-type formatting fires mid-word) would apply a range computed against text that is no longer
/// there. Every other stale read shows something wrong; this one writes something wrong.
/// </summary>
public class StaleFormatEditTests
{
    private const string Path = @"C:\bo3\share\raw\scripts\main.gsc";

    private static DocumentStore NewStore()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Applies an edit the way an editor would, to prove the result is the formatted text.</summary>
    private static string Apply(SourceText text, GscFormatter.FormatEdit edit)
    {
        int start = text.GetOffset(edit.Range.Start);
        int end = text.GetOffset(edit.Range.End);
        string original = text.Text;

        return string.Concat(original.AsSpan(0, start), edit.NewText, original.AsSpan(end));
    }

    [Fact]
    public void AnEditFromStaleTextCorruptsTheLiveDocument()
    {
        // The bug being fixed, demonstrated directly. The analysed text is badly indented near
        // the TOP; the live text has had a long line inserted above it, so every offset has
        // shifted.
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n", version: 1);
        ParseResult stale = store.Analyze(document);

        store.ApplyChange(document, range: null, "// a newly typed comment line\nfunction f()\n{\nx = 1;\n}\n", version: 2);

        GscFormatter.FormatEdit edit = GscFormatter.FormatMinimal(stale)!.Value;

        // Applying the stale edit to the live text does NOT produce correctly formatted source.
        string wrong = Apply(document.Text, edit);
        Assert.NotEqual(GscFormatter.Format(store.Analyze(document)), wrong);
    }

    [Fact]
    public void AnalyzingFirst_ProducesAnEditThatAppliesCleanly()
    {
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n", version: 1);
        store.Analyze(document);

        store.ApplyChange(document, range: null, "// a newly typed comment line\nfunction f()\n{\nx = 1;\n}\n", version: 2);

        // What the handlers now do.
        ParseResult fresh = store.AnalyzeIfStale(document);
        GscFormatter.FormatEdit edit = GscFormatter.FormatMinimal(fresh)!.Value;

        Assert.Equal(GscFormatter.Format(fresh), Apply(document.Text, edit));
    }

    [Fact]
    public void TheEditRangeIsWithinTheLiveText()
    {
        // The concrete danger: a range past the end of the live document, or spanning characters
        // that moved. Offsets from a fresh analysis are always in bounds by construction.
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n\n\n\n\nfunction g()\n{\ny = 2;\n}\n", version: 1);
        store.Analyze(document);

        store.ApplyChange(document, range: null, "function f()\n{\nx = 1;\n}\n", version: 2);

        ParseResult fresh = store.AnalyzeIfStale(document);
        GscFormatter.FormatEdit edit = GscFormatter.FormatMinimal(fresh)!.Value;

        Assert.True(document.Text.GetOffset(edit.Range.End) <= document.Text.Length);
    }
}
