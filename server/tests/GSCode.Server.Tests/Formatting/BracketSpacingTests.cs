using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Bracket interiors are padded like parentheses — `a[ i ]` — but adjacent brackets stay tight, so
/// a function pointer's `[[` and `]]` read as single tokens rather than as nested indexes.
///
/// This is a deliberate override of the corpus, which prefers tight indexes 19,175 to 4,686. One
/// padding rule for every bracket beats an asymmetry nobody remembers the direction of.
/// </summary>
public class BracketSpacingTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Body(string statements)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f( a, i, j, ptr )\n{\n\t" + statements + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return GscFormatter.Format(result, s_tabs)!;
    }

    [Theory]
    [InlineData("a[i] = 1;", "a[ i ] = 1;")]
    [InlineData("a[ i ] = 1;", "a[ i ] = 1;")]
    [InlineData("a[i][j] = 1;", "a[ i ][ j ] = 1;")]
    [InlineData("x = a[i].field;", "x = a[ i ].field;")]
    [InlineData("x = foo()[i];", "x = foo()[ i ];")]
    public void IndexBracketsArePadded(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[[ptr]]();", "[[ ptr ]]();")]
    [InlineData("[[ ptr ]]();", "[[ ptr ]]();")]
    [InlineData("self thread [[ptr]]( a );", "self thread [[ ptr ]]( a );")]
    public void AFunctionPointersDoubleBracketsStayTightAroundAPaddedInterior(string input, string expected)
    {
        // `[[` and `]]` are the token; the name inside is padded like any other bracket interior.
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a = [];", "a = [];")]
    [InlineData("a = [ ];", "a = [];")]
    public void AnEmptyArrayStaysTight(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrayLiteralKeepsTheSpaceAfterTheAssignment()
    {
        // The subscript rule must not swallow this: `[` here opens a literal, it does not index
        // the `=`. Getting it wrong produced `a =[];`.
        Assert.Contains("a = [];", Body("a = [];"), StringComparison.Ordinal);
    }

    [Fact]
    public void BracketSpacingIsIdempotent()
    {
        string once = Body("a[i] = [[ptr]]( a[j] );");

        ParseResult reparsed = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(once), NullInsertProvider.Instance, new NameTable());

        Assert.Equal(once, GscFormatter.Format(reparsed, s_tabs));
    }
}
