using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A line that starts inside an open parenthesis or bracket continues a statement, and sits one
/// level deeper than the line it continues. The formatter used to put it flush with the statement,
/// against the stock scripts, which indent continuations 438 times to 16.
/// </summary>
public class ContinuationIndentTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Format(string source)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, s_tabs) ?? throw new InvalidOperationException("formatter refused the input");
    }

    [Fact]
    public void AContinuedCallArgumentSitsOneLevelDeeper()
    {
        Assert.Equal(
            "function f()\n{\n\tfoo( a,\n\t\tb );\n}\n",
            Format("function f()\n{\nfoo( a,\nb );\n}\n"));
    }

    [Fact]
    public void ASplitConditionAlignsUnderTheHeadersParenthesis()
    {
        // The shape stock writes a long chain of conditions in (util_shared.csc): the breaks stay,
        // and each continuation line starts under the first character inside `if ( `, in spaces
        // after the header line's own tabs.
        Assert.Equal(
            "function f()\n{\n\tif ( ( !isdefined( a ) || a != \"x\" ) &&\n\t     ( !isdefined( b ) || b != \"x\" ) )\n\t\tc();\n}\n",
            Format("function f()\n{\nif ((!isdefined(a)||a!=\"x\")&&\n(!isdefined(b)||b!=\"x\"))\nc();\n}\n"));
    }

    [Fact]
    public void EveryLineOfASplitHeaderSharesOneColumn()
    {
        // A call nested in the header aligns with the header, not a level deeper.
        Assert.Equal(
            "function f()\n{\n\tif ( a &&\n\t     foo( b,\n\t     c ) )\n\t{\n\t\td();\n\t}\n}\n",
            Format("function f()\n{\nif ( a &&\nfoo( b,\nc ) )\n{\nd();\n}\n}\n"));
    }

    [Fact]
    public void AnUnbracedBodyAfterASplitHeaderIsIndentedOnce()
    {
        Assert.Equal(
            "function f()\n{\n\tif ( a ||\n\t     b )\n\t\tc();\n\n\td();\n}\n",
            Format("function f()\n{\nif ( a ||\nb )\nc();\nd();\n}\n"));
    }

    [Fact]
    public void AWhileHeaderAlignsUnderItsOwnParenthesis()
    {
        Assert.Equal(
            "function f()\n{\n\twhile ( a && // why\n\t        b )\n\t{\n\t}\n}\n",
            Format("function f()\n{\nwhile ( a && // why\nb )\n{\n}\n}\n"));
    }

    [Fact]
    public void AHeaderWhoseParenthesisEndsTheLineTakesTheOrdinaryContinuation()
    {
        Assert.Equal(
            "function f()\n{\n\tif (\n\t\ta && b )\n\t{\n\t}\n}\n",
            Format("function f()\n{\nif (\na && b )\n{\n}\n}\n"));
    }

    [Fact]
    public void NestedOpenGroupsStillCountAsOneLevel()
    {
        // One level however many groups are open, so a call inside a call does not march right.
        Assert.Equal(
            "function f()\n{\n\tfoo( bar( a,\n\t\tb ) );\n}\n",
            Format("function f()\n{\nfoo( bar( a,\nb ) );\n}\n"));
    }

    [Fact]
    public void AClosingParenthesisOnItsOwnLineReturnsToTheStatement()
    {
        Assert.Equal(
            "function f()\n{\n\tfoo(\n\t\ta,\n\t\tb\n\t);\n}\n",
            Format("function f()\n{\nfoo(\na,\nb\n);\n}\n"));
    }

    [Fact]
    public void AContinuedSubscriptIsIndentedLikeAnArgumentList()
    {
        Assert.Equal(
            "function f()\n{\n\ta[ b +\n\t\tc ] = 1;\n}\n",
            Format("function f()\n{\na[ b +\nc ] = 1;\n}\n"));
    }

    [Fact]
    public void ContinuationIndentIsIdempotent()
    {
        string once = Format("function f()\n{\nx = foo( a,\nbar( b,\nc ) );\n}\n");

        Assert.Equal(once, Format(once));
    }
}
