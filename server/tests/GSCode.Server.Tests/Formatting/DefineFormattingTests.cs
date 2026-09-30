using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Whether a <c>#define</c> takes parameters is decided by whether its '(' touches the name — a
/// fact about whitespace, which the token gate cannot see. The formatter used to hug any '(' after
/// a name, so <c>#define HALF ( 1 / 2 )</c> became function-like and every bare <c>HALF</c>
/// stopped expanding.
/// </summary>
public class DefineFormattingTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Format(string source)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, s_tabs) ?? throw new InvalidOperationException("formatter refused the input");
    }

    [Fact]
    public void AnObjectLikeMacroWhoseBodyStartsWithAParenStaysObjectLike()
    {
        Assert.StartsWith("#define HALF ( 1 / 2 )\n", Format("#define HALF ( 1 / 2 )\nfunction f()\n{\n\tx = HALF;\n}\n"));
    }

    [Fact]
    public void AFunctionLikeMacroIsSpacedLikeAFunction()
    {
        Assert.StartsWith("#define SCALE( _v ) ( _v * 2 )\n", Format("#define SCALE(_v)(_v*2)\nfunction f()\n{\n\tx = SCALE( 1 );\n}\n"));
    }

    [Fact]
    public void AParameterlessFunctionLikeMacroKeepsItsEmptyParens()
    {
        Assert.StartsWith("#define NOARGS() bar()\n", Format("#define NOARGS()bar()\nfunction f()\n{\n\tx = NOARGS();\n}\n"));
    }

    [Fact]
    public void AContinuationBackslashIsSetOffAndItsLineIndented()
    {
        // A path's backslashes still hug: only a '\' that ends its line is a continuation.
        Assert.StartsWith(
            "#using scripts\\shared\\util_shared;\n\n#define MULTI( _a ) \\\n\tbaz( _a )\n",
            Format("#using scripts\\shared\\util_shared;\n\n#define MULTI( _a )\\\nbaz( _a )\nfunction f()\n{\n\tx = MULTI( 1 );\n}\n"));
    }

    [Fact]
    public void DefineFormattingIsIdempotent()
    {
        string once = Format("#define HALF ( 1 / 2 )\n#define SCALE(_v)(_v*2)\nfunction f()\n{\n\tx = SCALE( HALF );\n}\n");

        Assert.Equal(once, Format(once));
    }
}
