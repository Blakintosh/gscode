using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Formatting is returned as one edit per changed region, not a single edit spanning the whole
/// document.
///
/// The reason is the caret. An editor keeps the caret across a format by mapping its offset through
/// the edits; a caret inside a replaced range has nowhere to map and snaps to the edit's end. A
/// document-wide reindent used to be one edit covering nearly everything, so the caret jumped. The
/// property that matters, and is asserted here, is that a line the formatter did not change is
/// never inside any edit — so the caret on it stays put.
/// </summary>
public class FormatMinimalEditsTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
    }

    /// <summary>Applies the edits the way an editor would — right to left, so earlier offsets hold.</summary>
    private static string Apply(string original, ImmutableArray<GscFormatter.FormatEdit> edits, SourceText text)
    {
        string result = original;
        foreach ( GscFormatter.FormatEdit edit in edits.OrderByDescending(e => text.GetOffset(e.Range.Start)) )
        {
            int start = text.GetOffset(edit.Range.Start);
            int end = text.GetOffset(edit.Range.End);
            result = string.Concat(result.AsSpan(0, start), edit.NewText, result.AsSpan(end));
        }

        return result;
    }

    [Fact]
    public void TheEditsReproduceTheFormattedDocument()
    {
        ParseResult result = Analyze("function f()\n{\na=1;\nb=2;\nc=3;\n}\n");

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(result.Text.Text, edits, result.Text));
    }

    [Fact]
    public void AnUnchangedLineBetweenTwoChangedOnesIsNotInsideAnyEdit()
    {
        // The caret-preservation property. The middle line is already correct; reformatting the
        // lines around it must leave it out of every edit range, or the caret resting on it moves.
        ParseResult result = Analyze("function f()\n{\na=1;\n\tx = 1;\nc=3;\n}\n");
        SourceText text = result.Text;

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        // Line 3 (0-based) is the already-correct `\tx = 1;`.
        Assert.All(edits, edit =>
        {
            bool coversLine3 = edit.Range.Start.Line <= 3 && edit.Range.End.Line > 3;
            Assert.False(coversLine3, "an edit spans the unchanged line the caret could be on");
        });

        // And it still produces the right output.
        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(text.Text, edits, text));
    }

    [Fact]
    public void ManySeparatelyChangedLinesBecomeManyEdits()
    {
        // Interleaved changed and unchanged lines: the point is that this is NOT one big edit.
        ParseResult result = Analyze(
            "function f()\n{\n\ta = 1;\nb=2;\n\tc = 3;\nd=4;\n\te = 5;\n}\n");

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        Assert.True(edits.Length >= 2, "interleaved changes should be separate edits, not one span");
        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(result.Text.Text, edits, result.Text));
    }

    [Fact]
    public void EditsAreOrderedAndDoNotOverlap()
    {
        // The LSP requires it, and the diff guarantees it: a matched line always sits between hunks.
        ParseResult result = Analyze(
            "function f()\n{\na=1;\n\tok = 1;\nb=2;\n\tok2 = 2;\nc=3;\n}\n");
        SourceText text = result.Text;

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        int previousEnd = -1;
        foreach ( GscFormatter.FormatEdit edit in edits )
        {
            int start = text.GetOffset(edit.Range.Start);
            int end = text.GetOffset(edit.Range.End);
            Assert.True(start >= previousEnd, "edits overlap or are out of order");
            previousEnd = end;
        }
    }

    [Fact]
    public void AWholeDocumentReindentLeavesBlankLinesUntouched()
    {
        // A pasted-flat function: every code line reindents, but the blank line separating two
        // statements is unchanged and must escape every edit.
        ParseResult result = Analyze(
            "function f()\n{\na = 1;\n\nb = 2;\n}\n");
        SourceText text = result.Text;

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        // Line 3 is the blank line.
        Assert.All(edits, edit =>
            Assert.False(edit.Range.Start.Line <= 3 && edit.Range.End.Line > 3));
        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(text.Text, edits, text));
    }

    [Fact]
    public void AnAlreadyFormattedDocumentYieldsNoEdits()
    {
        Assert.Empty(GscFormatter.FormatMinimalEdits(Analyze("function f()\n{\n\ta = 1;\n}\n"), s_tabs));
    }

    [Fact]
    public void APureInsertionIsAZeroWidthEdit()
    {
        // Directive sorting inserts a blank line between groups; the insertion point is a
        // zero-width range, not a replacement of the surrounding lines.
        ParseResult result = Analyze(
            "#using scripts\\a;\n#namespace foo;\n\nfunction f()\n{\n\tx = 1;\n}\n");
        SourceText text = result.Text;

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(result, s_tabs);

        Assert.NotEmpty(edits);
        Assert.Equal(GscFormatter.Format(result, s_tabs), Apply(text.Text, edits, text));
    }
}
