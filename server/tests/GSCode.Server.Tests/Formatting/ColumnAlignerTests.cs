using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Consecutive alignment of the INTERIOR of subscripts and call arguments: a run of same-shape
/// statements has each bracket column and argument column padded to its widest, and the last
/// argument and the right-hand side — the free cells — are left alone.
/// </summary>
public class ColumnAlignerTests
{
    private static readonly FormatOptions s_aligned =
        FormatOptions.Default with { UseTabs = true, AlignConsecutive = true };

    private static string Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, s_aligned)!;
    }

    [Fact]
    public void ArraySubscriptColumnsAlign()
    {
        // The reported example. Each subscript is padded so its ']' lines up, and the operator
        // then lines up because the whole left-hand side is now the same width.
        string formatted = Format("""
            function f()
            {
            	foo[ "lol" ][ "lol2" ] = "something";
            	foo[ "somethingelse" ][ "other" ] = "garbage";
            }
            """);

        const string expected = "function f()\n"
            + "{\n"
            + "\tfoo[ \"lol\"           ][ \"lol2\"  ] = \"something\";\n"
            + "\tfoo[ \"somethingelse\" ][ \"other\" ] = \"garbage\";\n"
            + "}\n";

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void CallArgumentColumnsAlign_ExceptTheLast()
    {
        // The other reported example. Arguments followed by a comma align; the final argument,
        // followed by ')', is free and keeps its natural width.
        string formatted = Format("""
            function f()
            {
            	register( "toplayer", PARASITE_ROUND_RING_FX, VERSION_SHIP, 1, "counter" );
            	register( "world", "toggle_on_parasite_fog", VERSION_SHIP, 2, "int" );
            }
            """);

        const string expected = "function f()\n"
            + "{\n"
            + "\tregister( \"toplayer\", PARASITE_ROUND_RING_FX  , VERSION_SHIP, 1, \"counter\" );\n"
            + "\tregister( \"world\"   , \"toggle_on_parasite_fog\", VERSION_SHIP, 2, \"int\" );\n"
            + "}\n";

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void ADifferentCalleeIsNotTheSameShape()
    {
        // register and spawn do not align with each other, even adjacent.
        string formatted = Format("""
            function f()
            {
            	register( "a", 1 );
            	spawn( "bb", 2 );
            }
            """);

        Assert.Contains("\tregister( \"a\", 1 );\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tspawn( \"bb\", 2 );\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentArityIsNotTheSameShape()
    {
        string formatted = Format("""
            function f()
            {
            	register( "a", 1 );
            	register( "bb", 2, 3 );
            }
            """);

        // Two-arg and three-arg calls form separate groups of one, so nothing is padded.
        Assert.Contains("\tregister( \"a\", 1 );\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tregister( \"bb\", 2, 3 );\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentSubscriptBaseLeavesTheInteriorsAlone()
    {
        // Different bases are not the same shape, so ColumnAligner does NOT pad the subscript
        // interiors. The '=' still aligns -- that is the operator aligner doing its own job across
        // consecutive assignments -- but "a" is not widened to "bbbb".
        string formatted = Format("""
            function f()
            {
            	foo[ "a" ] = 1;
            	bar[ "bbbb" ] = 2;
            }
            """);

        Assert.Contains("foo[ \"a\" ]", formatted, StringComparison.Ordinal);
        Assert.Contains("bar[ \"bbbb\" ]", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommentIsTransparentAcrossAGroup()
    {
        string formatted = Format("""
            function f()
            {
            	register( "a", 1 );
            	// a note
            	register( "bbbb", 2 );
            }
            """);

        Assert.Contains("\tregister( \"a\"   , 1 );\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\t// a note\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tregister( \"bbbb\", 2 );\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankLineEndsAGroup()
    {
        string formatted = Format("""
            function f()
            {
            	register( "a", 1 );

            	register( "bbbb", 2 );
            }
            """);

        // Two groups of one: nothing padded.
        Assert.Contains("\tregister( \"a\", 1 );\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tregister( \"bbbb\", 2 );\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AlignmentIsIdempotent()
    {
        // The property the whole design turns on: replacing gaps, not inserting into them.
        string once = Format("""
            function f()
            {
            	foo[ "lol" ][ "lol2" ] = "something";
            	foo[ "somethingelse" ][ "other" ] = "garbage";
            	register( "a", 1, "x" );
            	register( "bbbb", 22, "y" );
            }
            """);

        ParseResult reparsed = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(once), NullInsertProvider.Instance, new NameTable());

        Assert.Equal(once, GscFormatter.Format(reparsed, s_aligned));
    }

    [Fact]
    public void OffByDefault()
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\nfoo[ \"lol\" ] = 1;\nfoo[ \"somethingelse\" ] = 2;\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        string formatted = GscFormatter.Format(result, FormatOptions.Default with { UseTabs = true })!;

        Assert.Contains("\tfoo[ \"lol\" ] = 1;\n", formatted, StringComparison.Ordinal);
    }
}
