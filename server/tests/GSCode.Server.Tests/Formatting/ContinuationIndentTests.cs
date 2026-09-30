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
    public void ASplitConditionIsJoinedOntoTheHeaderLine()
    {
        // A header is one line, calls inside it included, so it takes no continuation at all.
        Assert.Equal(
            "function f()\n{\n\tif ( a && foo( b, c ) )\n\t{\n\t\td();\n\t}\n}\n",
            Format("function f()\n{\nif ( a &&\nfoo( b,\nc ) )\n{\nd();\n}\n}\n"));
    }

    [Fact]
    public void AnUnbracedBodyAfterAJoinedHeaderIsIndentedOnce()
    {
        Assert.Equal(
            "function f()\n{\n\tif ( a || b )\n\t\tc();\n\td();\n}\n",
            Format("function f()\n{\nif ( a ||\nb )\nc();\nd();\n}\n"));
    }

    [Fact]
    public void ALineCommentInsideAHeaderStillEndsItsLine()
    {
        Assert.Equal(
            "function f()\n{\n\twhile ( a && // why\n\t\tb )\n\t{\n\t}\n}\n",
            Format("function f()\n{\nwhile ( a && // why\nb )\n{\n}\n}\n"));
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
