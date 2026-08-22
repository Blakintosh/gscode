using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Operators take a space on each side, and an opening delimiter after one is an OPERAND rather
/// than something being called or indexed.
///
/// That distinction is the whole of this file. `foo(` and `a[` hug because the name is being
/// called or subscripted; `= (`, `+= [`, `|| (` do not, because there is nothing there to call.
/// Missing it produced `x =( GetDvarString…` and `a =[];` — both from the same wrong assumption
/// that a '(' or '[' always binds to whatever precedes it.
/// </summary>
public class OperatorSpacingTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Body(string statement)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f( a, b, level )\n{\n\t" + statement + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        string? formatted = GscFormatter.Format(result, s_tabs);
        Assert.NotNull(formatted);
        return formatted;
    }

    [Fact]
    public void AParenthesisedExpressionAfterAssignmentKeepsItsSpace()
    {
        // The reported bug.
        Assert.Contains(
            "level.xenon = ( GetDvarString( \"xenonGame\" ) == \"true\" );",
            Body("level.xenon = (GetDvarString( \"xenonGame\") == \"true\");"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a = (b);", "a = ( b );")]
    [InlineData("a += (b);", "a += ( b );")]
    [InlineData("a = b + (a);", "a = b + ( a );")]
    [InlineData("a = !(b);", "a = !( b );")]
    [InlineData("a = (a) + (b);", "a = ( a ) + ( b );")]
    public void AGroupingParenIsSpacedFromTheOperatorBeforeIt(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("foo();", "foo();")]
    [InlineData("foo( a );", "foo( a );")]
    [InlineData("a = foo( b );", "a = foo( b );")]
    [InlineData("a thread foo( b );", "a thread foo( b );")]
    [InlineData("a = ns::foo( b );", "a = ns::foo( b );")]
    public void ACallStillHugsItsCallee(string input, string expected)
    {
        // The exemption must not cost us the case the hugging rule was built for.
        string formatted = Body(input);

        Assert.Contains(expected, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("foo (", formatted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a += b;", "a += b;")]
    [InlineData("a -= b;", "a -= b;")]
    [InlineData("a *= b;", "a *= b;")]
    [InlineData("a /= b;", "a /= b;")]
    [InlineData("a %= b;", "a %= b;")]
    [InlineData("a |= b;", "a |= b;")]
    [InlineData("a &= b;", "a &= b;")]
    [InlineData("a ^= b;", "a ^= b;")]
    [InlineData("a <<= b;", "a <<= b;")]
    [InlineData("a >>= b;", "a >>= b;")]
    public void CompoundAssignmentsAreSpacedOnBothSides(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a+=b;", "a += b;")]
    [InlineData("a-=b;", "a -= b;")]
    [InlineData("a*=b;", "a *= b;")]
    [InlineData("a|=b;", "a |= b;")]
    public void ATightCompoundAssignmentIsSpacedOut(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a += [];", "a += [];")]
    [InlineData("a = [];", "a = [];")]
    public void AnArrayLiteralAfterAnAssignmentKeepsItsSpace(string input, string expected)
    {
        Assert.Contains(expected, Body(input), StringComparison.Ordinal);
    }

    [Fact]
    public void SubscriptsAndCallsStillHugAfterAllOfThis()
    {
        Assert.Contains("a = b[ 0 ] + foo( 1 );", Body("a = b[0]+foo(1);"), StringComparison.Ordinal);
    }
}
