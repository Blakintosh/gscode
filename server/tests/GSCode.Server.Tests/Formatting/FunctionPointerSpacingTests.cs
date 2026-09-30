using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A function pointer's <c>[[</c> and <c>]]</c> each read as one token: the pair stays together,
/// the interior is padded like any bracket, and a caller before it is set apart —
/// <c>self [[ foo ]]()</c>. It used to hug the caller like a subscript, and to glue every
/// <c>] ]</c>, including the ones closing nested subscripts.
/// </summary>
public class FunctionPointerSpacingTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Body(string statement)
    {
        ParseResult result = TestParse.Analyze("function f()\n{\n" + statement + "\n}\n");
        string formatted = GscFormatter.Format(result, s_tabs) ?? throw new InvalidOperationException("formatter refused the input");

        return formatted.Split('\n')[2].Trim();
    }

    [Theory]
    [InlineData("self[[level.callback]]();", "self [[ level.callback ]]();")]
    [InlineData("self thread [[ptr]](1,2);", "self thread [[ ptr ]]( 1, 2 );")]
    [InlineData("[[ptr]]();", "[[ ptr ]]();")]
    [InlineData("[[obj]]->method(5);", "[[ obj ]]->method( 5 );")]
    [InlineData("x=[[ptr]]();", "x = [[ ptr ]]();")]
    [InlineData("foo([[ptr]]());", "foo( [[ ptr ]]() );")]
    public void APointerCallIsSpacedFromItsCallerAndPaddedInside(string source, string expected)
    {
        Assert.Equal(expected, Body(source));
    }

    [Theory]
    [InlineData("x=a[b[c]];", "x = a[ b[ c ] ];")]
    [InlineData("x=a[i];", "x = a[ i ];")]
    [InlineData("x=[];", "x = [];")]
    public void SubscriptsStayPadded(string source, string expected)
    {
        Assert.Equal(expected, Body(source));
    }
}
