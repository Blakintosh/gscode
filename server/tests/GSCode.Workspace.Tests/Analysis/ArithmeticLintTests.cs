using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Division by a divisor written as zero.
///
/// 1.5 caught the indirect form too (`d = 0; x = n / d;`) because it tracked constant values through
/// a data-flow pass. This tree has no constant propagation, so the rule is deliberately the literal
/// case only — narrower, and certain where the wider one needed a lattice to be.
/// </summary>
public class ArithmeticLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function f( n, d )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // Without this a syntax slip in a test case yields an empty tree, and every Assert.Empty
        // below passes while proving nothing.
        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        return NodeLintHarness.Run(result, ArithmeticLint.InspectNode);
    }

    [Theory]
    [InlineData("    x = n / 0;")]
    [InlineData("    x = n % 0;")]
    [InlineData("    x = n / 0.0;")]
    [InlineData("    x = n / .0;")]
    [InlineData("    x = n / 0x0;")]
    [InlineData("    x = n / ( 0 );")]
    [InlineData("    x /= 0;")]
    [InlineData("    x %= 0;")]
    public void AZeroDivisorIsReported(string body)
    {
        Assert.Single(Lint(body), d => d.Code == GscDiagnosticCode.DivisionByZero);
    }

    [Theory]
    [InlineData("    x = n / d;")]
    [InlineData("    x = n / 1;")]
    [InlineData("    x = n / 0.5;")]
    [InlineData("    x = n / 0x10;")]
    [InlineData("    x = 0 / n;")]
    [InlineData("    x = n * 0;")]
    [InlineData("    x = n + 0;")]
    public void EverythingElseIsLeftAlone(string body)
    {
        Assert.Empty(Lint(body));
    }

    [Fact]
    public void AZeroNumeratorIsNotADivisionByZero()
    {
        // `0 / n` is fine and only the right operand is the divisor. A rule matching on "a zero
        // somewhere in a division" would report it.
        Assert.Empty(Lint("    x = 0 / n;"));
    }

    [Fact]
    public void AVariableThatHoldsZeroIsNotReported()
    {
        // The deliberate limit. Catching this needs constant propagation, which this tree does not
        // have — and a rule that guessed would be wrong the moment `d` is reassigned on some path.
        Assert.Empty(Lint("    d = 0;\n    x = n / d;"));
    }

    [Fact]
    public void ItIsReportedOnTheDivisorRatherThanTheWholeExpression()
    {
        Diagnostic reported = Assert.Single(Lint("    x = n / 0;"));

        Assert.Equal("    x = n / ".Length, reported.Range.Start.Character);
    }

    [Fact]
    public void ADivisionNestedInsideACallIsFound()
    {
        Assert.Single(Lint("    use( 1 + n / 0 );"), d => d.Code == GscDiagnosticCode.DivisionByZero);
    }
}
