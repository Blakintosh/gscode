using System.Collections.Immutable;
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
/// FormatMinimalEdits diffs the formatted output against the analysed text, so its ranges are
/// offsets INTO that text. Analysis is debounced 250 ms behind the keystrokes, so a format arriving
/// in that window (format-on-save fires right after edits, and on-type formatting fires mid-word)
/// would apply ranges computed against text that is no longer there. Every other stale read shows
/// something wrong; this one writes something wrong.
/// </summary>
public class StaleFormatEditTests
{
    private static readonly string Path = TestPaths.Raw(@"scripts\main.gsc");

    private static DocumentStore NewStore()
    {
        return new DocumentStore(static _ => NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Applies edits the way an editor would, to prove the result is the formatted text.</summary>
    private static string Apply(SourceText text, ImmutableArray<GscFormatter.FormatEdit> edits)
    {
        string result = text.Text;

        // Applied back to front so an earlier edit's offsets are not shifted by a later one —
        // FormatMinimalEdits returns them in document order, front to back.
        for ( int index = edits.Length - 1; index >= 0; index-- )
        {
            GscFormatter.FormatEdit edit = edits[index];
            int start = text.GetOffset(edit.Range.Start);
            int end = text.GetOffset(edit.Range.End);
            result = string.Concat(result.AsSpan(0, start), edit.NewText, result.AsSpan(end));
        }

        return result;
    }

    [Fact]
    public void EditsFromStaleTextCorruptTheLiveDocument()
    {
        // The bug being fixed, demonstrated directly. The analysed text is badly indented near
        // the TOP; the live text has had a long line inserted above it, so every offset has
        // shifted.
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n", version: 1);
        ParseResult stale = store.Analyze(document);

        store.ApplyChange(document, range: null, "// a newly typed comment line\nfunction f()\n{\nx = 1;\n}\n", version: 2);

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(stale);

        // Applying the stale edits to the live text does NOT produce correctly formatted source.
        string wrong = Apply(document.Text, edits);
        Assert.NotEqual(GscFormatter.Format(store.Analyze(document)), wrong);
    }

    [Fact]
    public void AnalyzingFirst_ProducesEditsThatApplyCleanly()
    {
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n", version: 1);
        store.Analyze(document);

        store.ApplyChange(document, range: null, "// a newly typed comment line\nfunction f()\n{\nx = 1;\n}\n", version: 2);

        // What the handlers now do.
        ParseResult fresh = store.AnalyzeIfStale(document);
        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(fresh);

        Assert.Equal(GscFormatter.Format(fresh), Apply(document.Text, edits));
    }

    [Fact]
    public void TheEditRangesAreWithinTheLiveText()
    {
        // The concrete danger: a range past the end of the live document, or spanning characters
        // that moved. Offsets from a fresh analysis are always in bounds by construction.
        DocumentStore store = NewStore();
        OpenDocument document = store.Open(Path, "function f()\n{\nx = 1;\n}\n\n\n\n\nfunction g()\n{\ny = 2;\n}\n", version: 1);
        store.Analyze(document);

        store.ApplyChange(document, range: null, "function f()\n{\nx = 1;\n}\n", version: 2);

        ParseResult fresh = store.AnalyzeIfStale(document);
        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(fresh);

        Assert.NotEmpty(edits);
        foreach ( GscFormatter.FormatEdit edit in edits )
        {
            Assert.True(document.Text.GetOffset(edit.Range.End) <= document.Text.Length);
        }
    }
}
