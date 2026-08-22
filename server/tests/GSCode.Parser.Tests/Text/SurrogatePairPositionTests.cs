using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Text;

/// <summary>
/// LSP positions are UTF-16 code units, and the server declares `positionEncoding: utf-16` to
/// say so. An astral character (emoji) is TWO code units, so anything counting characters as
/// runes would drift by one per emoji and mis-place every range after it on the line.
/// </summary>
public class SurrogatePairPositionTests
{
    // U+1F600 GRINNING FACE — a surrogate pair, so string.Length counts it as 2.
    private const string Emoji = "\U0001F600";

    [Fact]
    public void EmojiCountsAsTwoCodeUnits()
    {
        Assert.Equal(2, Emoji.Length);
    }

    [Fact]
    public void PositionAfterAnEmoji_CountsCodeUnitsNotRunes()
    {
        SourceText text = SourceText.From($"x = \"{Emoji}\";\ny = 1;");

        // Line 0 is: x = " <emoji> " ;  →  the closing quote sits at code unit 7, not 6.
        int offsetOfSemicolon = text.Text.IndexOf(';', StringComparison.Ordinal);
        Position semicolon = text.GetPosition(offsetOfSemicolon);

        Assert.Equal(0, semicolon.Line);
        Assert.Equal(8, semicolon.Character);
    }

    [Fact]
    public void OffsetAndPositionRoundTripAcrossAnEmoji()
    {
        SourceText text = SourceText.From($"a = \"{Emoji}{Emoji}\";");

        for ( int offset = 0; offset <= text.Length; offset++ )
        {
            Position position = text.GetPosition(offset);
            Assert.Equal(offset, text.GetOffset(position));
        }
    }

    [Fact]
    public void LineStartsAreCorrectAfterALineContainingAnEmoji()
    {
        SourceText text = SourceText.From($"// {Emoji}\nfunction f()\n{{\n}}\n");

        // The declaration must still be reported on line 1 despite the emoji above it.
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, text, NullInsertProvider.Instance, new NameTable());

        FunctionSymbol function = Assert.Single(result.Extraction.Functions);
        Assert.Equal(1, function.NameRange.Start.Line);
    }

    [Fact]
    public void RangesAfterAnEmojiOnTheSameLine_AreNotShifted()
    {
        // The emoji sits inside a string literal before the call, so a rune-based count would
        // report the call one column early.
        SourceText text = SourceText.From($"function f()\n{{\n    s = \"{Emoji}\"; helper();\n}}\n");
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, text, NullInsertProvider.Instance, new NameTable());

        ReferenceEntry call = Assert.Single(
            result.Extraction.References,
            entry => string.Equals(entry.Key.Name, "helper", StringComparison.OrdinalIgnoreCase));

        string line = text.Text.Split('\n')[2];
        Assert.Equal(line.IndexOf("helper", StringComparison.Ordinal), call.Range.Start.Character);
    }
}
