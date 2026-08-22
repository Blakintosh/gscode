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
/// A statement that computes a value and drops it.
///
/// The rule reports only when the expression contains no effectful node ANYWHERE, which is weaker
/// than judging the top node and deliberately so: `a ? foo() : bar();` and `flag &amp;&amp; start();`
/// both run something while having a ternary and a binary on top. GSC has no compiler to contradict
/// a false report, so precision is what gets given up.
/// </summary>
public class ExpressionStatementLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function f( a, b )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // Several cases below are only interesting if they PARSED. `a + b;` yielding a parse error
        // would make Assert.Single fail loudly, but `foo();` yielding one would make Assert.Empty
        // pass for the wrong reason.
        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        return ExpressionStatementLint.Analyze(result);
    }

    [Theory]
    [InlineData("    a + b;")]
    [InlineData("    a;")]
    [InlineData("    a == b;")]
    [InlineData("    self.health;")]
    [InlineData("    a[ 0 ];")]
    [InlineData("    1;")]
    [InlineData("    !a;")]
    [InlineData("    ( a + b );")]
    [InlineData("    a ? b : 1;")]
    public void AStatementThatCannotDoAnythingIsReported(string body)
    {
        Diagnostic reported = Assert.Single(
            Lint(body), d => d.Code == GscDiagnosticCode.InvalidExpressionStatement);

        // The reader can see the line does nothing; what they need is why it ended up that way.
        Assert.Contains("missing", reported.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    foo();")]
    [InlineData("    self foo();")]
    [InlineData("    self thread foo();")]
    [InlineData("    a = 1;")]
    [InlineData("    a += 1;")]
    [InlineData("    a++;")]
    [InlineData("    a--;")]
    [InlineData("    a[ 0 ] = 1;")]
    [InlineData("    self.health = 100;")]
    public void AnythingWithAnEffectIsLeftAlone(string body)
    {
        Assert.Empty(Lint(body));
    }

    [Theory]
    [InlineData("    a ? foo() : bar();")]
    [InlineData("    a ? foo() : 1;")]
    [InlineData("    a && start();")]
    [InlineData("    ( foo() );")]
    [InlineData("    foo()[ 0 ];")]
    [InlineData("    foo().field;")]
    [InlineData("    -foo();")]
    public void ACallBuriedInsideTheExpressionCountsAsAnEffect(string body)
    {
        // The whole reason the test is "contains no effectful node" rather than "the top node is not
        // effectful". Each of these runs something while having a ternary, binary, paren, index,
        // member or prefix on top.
        Assert.Empty(Lint(body));
    }

    [Fact]
    public void AForIncrementThatIncrementsNothingIsReported()
    {
        // The parser wraps a for-loop's initializer and increment in ExprStatementNode, so they get
        // the same rule — and `for ( i = 0; i < 3; i )` is a real way to write an infinite loop.
        Assert.Single(
            Lint("    for ( i = 0; i < 3; i ) { }"),
            d => d.Code == GscDiagnosticCode.InvalidExpressionStatement);
    }

    [Fact]
    public void AnOrdinaryForLoopIsLeftAlone()
    {
        Assert.Empty(Lint("    for ( i = 0; i < 3; i++ ) { }"));
    }

    [Fact]
    public void AnEmptyStatementIsNotAnExpressionStatement()
    {
        // A lone `;` is legal and ignored, and parses to its own node rather than an empty
        // expression — so it must not arrive here at all.
        Assert.Empty(Lint("    ;"));
    }

    [Fact]
    public void AFileTheParserCouldNotReadIsLeftAlone()
    {
        // The corpus wrote this gate. Before it the rule reported nine statements across the five
        // games and every one was recovery wreckage rather than a real no-effect statement — bo1's
        // `= % o_full_interstitial_01_camera;` splits into an assignment and a bare identifier, and
        // the identifier is what got reported.
        //
        // The `a + b;` here would be reported on its own; the unclosed call on the line above is
        // what silences the file.
        string source = "function f( a, b )\n{\n    foo( ;\n    a + b;\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // The premise: this file really did fail to parse.
        Assert.Contains(result.Tree.Diagnostics, d => (int)d.Code is >= 3000 and < 4000);
        Assert.Empty(ExpressionStatementLint.Analyze(result));
    }

    [Fact]
    public void TheSameStatementIsReportedWhenTheFileParses()
    {
        // The control for the gate. Without it "return nothing on a parse error" could be satisfied
        // by returning nothing at all.
        Assert.Single(Lint("    a + b;"), d => d.Code == GscDiagnosticCode.InvalidExpressionStatement);
    }

    [Fact]
    public void AStatementInsideANestedBlockIsFound()
    {
        Assert.Single(
            Lint("    if ( a )\n    {\n        while ( b )\n        {\n            a + b;\n        }\n    }"),
            d => d.Code == GscDiagnosticCode.InvalidExpressionStatement);
    }
}
