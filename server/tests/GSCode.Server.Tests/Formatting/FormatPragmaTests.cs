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
/// `#pragma disable format` leaves a hand-laid-out region alone.
///
/// Implemented by dropping EDITS that touch a protected region rather than by formatting
/// differently, which is what makes it safe: the formatter still runs over the whole file and
/// still passes its corruption guard on the whole file, so a bug here can leave code unformatted
/// but can never rewrite it wrongly.
/// </summary>
public class FormatPragmaTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    [Fact]
    public void AProtectedRegionIsLeftAlone()
    {
        // The badly-indented lines inside the region would normally be reflowed.
        string source =
            "function f()\n"
            + "{\n"
            + "// #pragma disable format\n"
            + "        a = 1;\n"
            + "            b = 2;\n"
            + "// #pragma restore format\n"
            + "}\n";

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(Analyze(source));

        // Nothing may touch lines 3 or 4, the two inside the region.
        Assert.All(edits, edit => Assert.True(
            edit.Range.End.Line < 3 || edit.Range.Start.Line > 4,
            $"an edit spanning lines {edit.Range.Start.Line}-{edit.Range.End.Line} reached into the protected region"));
    }

    [Fact]
    public void CodeOutsideTheRegionIsStillFormatted()
    {
        // The pragma protects a region, not the file. Switching the formatter off everywhere by
        // writing it once would be a trap.
        string source =
            "function f()\n"
            + "{\n"
            + "// #pragma disable format\n"
            + "        a = 1;\n"
            + "// #pragma restore format\n"
            + "            b = 2;\n"
            + "}\n";

        ImmutableArray<GscFormatter.FormatEdit> edits = GscFormatter.FormatMinimalEdits(Analyze(source));

        // Line 5 is below the region and must still be reindented. The edit covering it may begin
        // on line 4 — the `restore` comment is itself reindented, and consecutive changed lines
        // form one hunk — so this asks which lines are COVERED rather than where an edit starts.
        Assert.Contains(edits, edit => edit.Range.Start.Line <= 5 && edit.Range.End.Line >= 5);
    }

    [Fact]
    public void AFileWithNoPragmaIsUnaffected()
    {
        // The filter must cost nothing in the ordinary case.
        string source = "function f()\n{\n        a = 1;\n}\n";

        Assert.NotEmpty(GscFormatter.FormatMinimalEdits(Analyze(source)));
    }
}
