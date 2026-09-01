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
/// The two promises a `const` makes: the value is known at compile time (5029) and nothing changes
/// it afterwards (5030).
///
/// The interesting half is what counts as "known". Black Ops III's stock scripts hold 117 const
/// declarations and the non-literal ones are all arithmetic — `64 * 64`, `40.0 * 40.0` — so a rule
/// that only accepted bare literals would report shipped code. Those shapes are pinned below.
/// </summary>
public class ConstDeclarationLintTests
{
    /// <summary>
    /// Both halves the server runs for this rule: the per-node judgement over the shared walk, and
    /// `InspectRest`, which `WorkspaceLints` calls once per file outside that walk.
    /// </summary>
    private static ImmutableArray<Diagnostic> RunRule(ParseResult result)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(NodeLintHarness.Run(result, ConstDeclarationLint.InspectNode));
        ConstDeclarationLint.InspectRest(result, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function f( a )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // A parse error would make every assertion below meaningless — an empty tree reports nothing
        // and every Assert.Empty passes. `const` is Black Ops III's, which is the test default.
        Assert.DoesNotContain(result.AllDiagnostics, d => (int)d.Code is >= 3000 and < 4000);

        return RunRule(result);
    }

    // --- 5029 ---

    [Theory]
    [InlineData("    const MAX = 4;")]
    [InlineData("    const NAME = \"ready\";")]
    [InlineData("    const HALF = .5;")]
    [InlineData("    const OFF = -1;")]
    [InlineData("    const AREA = 64 * 64;")]
    [InlineData("    const RANGE = 40.0 * 40.0;")]
    [InlineData("    const NESTED = ( 2 + 3 ) * 4;")]
    [InlineData("    const HERE = ( 0, 0, 1 );")]
    [InlineData("    const ON = true;")]
    [InlineData("    const NOTHING = undefined;")]
    [InlineData("    const EMPTY = [];")]
    [InlineData("    const HASH = #\"event\";")]
    public void TheShapesTheStockScriptsUseAreAccepted(string body)
    {
        Assert.DoesNotContain(Lint(body), d => d.Code == GscDiagnosticCode.ExpectedConstantExpression);
    }

    [Theory]
    [InlineData("    const V = get_value();")]
    [InlineData("    const V = a;")]
    [InlineData("    const V = level.thing;")]
    [InlineData("    const V = a[ 0 ];")]
    [InlineData("    const V = 2 * get_value();")]
    public void AValueTheCompilerCannotFoldIsReported(string body)
    {
        Diagnostic reported = Assert.Single(
            Lint(body), d => d.Code == GscDiagnosticCode.ExpectedConstantExpression);

        // The message names what IS allowed; "not constant" alone leaves the reader wondering
        // whether the arithmetic above them counts.
        Assert.Contains("arithmetic over literals", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsReportedOnTheValueRatherThanTheWholeDeclaration()
    {
        // The name is fine; the value is the mistake, and that is what should be squiggled.
        Diagnostic reported = Assert.Single(
            Lint("    const V = get_value();"), d => d.Code == GscDiagnosticCode.ExpectedConstantExpression);

        Assert.Equal("const V = ".Length + 4, reported.Range.Start.Character);
    }

    [Fact]
    public void AMacroSuppliedValueIsJudgedOnWhatItExpandedTo()
    {
        // The preprocessor has already substituted it, so a squiggle would point into an expansion
        // rather than at anything the author wrote — the same call CaseLabelLint makes.
        string source = "#define LIMIT 8\nfunction f()\n{\n    const MAX = LIMIT;\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(
            RunRule(result),
            d => d.Code == GscDiagnosticCode.ExpectedConstantExpression);
    }

    // --- 5030 ---

    [Fact]
    public void AssigningToAConstantIsReported()
    {
        // The control.
        Diagnostic reported = Assert.Single(
            Lint("    const MAX = 4;\n    MAX = 8;"), d => d.Code == GscDiagnosticCode.CannotAssignToConstant);

        Assert.Contains("MAX", reported.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    const MAX = 4;\n    MAX += 1;")]
    [InlineData("    const MAX = 4;\n    MAX++;")]
    [InlineData("    const MAX = 4;\n    MAX--;")]
    public void EveryFormOfWriteCounts(string body)
    {
        Assert.Single(Lint(body), d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }

    [Fact]
    public void ReadingAConstantIsFine()
    {
        // The control's opposite. Without it the rule could be "passing" by reporting every mention.
        Assert.DoesNotContain(
            Lint("    const MAX = 4;\n    x = MAX + 1;\n    use( MAX );"),
            d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }

    [Fact]
    public void AnOrdinaryLocalIsNotReported()
    {
        Assert.DoesNotContain(
            Lint("    max = 4;\n    max = 8;"), d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }

    [Fact]
    public void WritingThroughAConstantIsNotReported()
    {
        // `MAX[ 0 ] = v` writes to what the constant holds, not to the binding. Whether that is legal
        // is a question about the value's type, which this rule does not have.
        Assert.DoesNotContain(
            Lint("    const MAX = 4;\n    MAX[ 0 ] = 1;\n    MAX.field = 2;"),
            d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }

    [Fact]
    public void ALocalInAnotherFunctionSharingTheNameIsNotReported()
    {
        // The corpus wrote this test. Collecting constant names file-wide reported ten writes across
        // Black Ops III's shipped scripts, every one an ordinary local that happened to share a name
        // with a constant declared in a different function.
        //
        // scripts\zm\gametypes\_hud_message.gsc is the case verbatim: `const duration = 60000;` in
        // one function, and a plain `duration = 60000;` in four others.
        string source =
            "function a()\n{\n    const duration = 60000;\n    use( duration );\n}\n"
            + "function b()\n{\n    duration = 60000;\n    use( duration );\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.DoesNotContain(
            RunRule(result),
            d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }

    [Fact]
    public void TheRuleStillFiresInsideTheDeclaringFunction()
    {
        // The control for the scoping above: narrowing to the function must not switch the rule off.
        string source =
            "function a()\n{\n    const duration = 60000;\n    duration = 1;\n}\n"
            + "function b()\n{\n    duration = 60000;\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Diagnostic reported = Assert.Single(
            RunRule(result),
            d => d.Code == GscDiagnosticCode.CannotAssignToConstant);

        // Line 3 (zero-based) is `duration = 1;` in function a, not the write in function b.
        Assert.Equal(3, reported.Range.Start.Line);
    }

    [Fact]
    public void TheDeclarationItselfIsNotAnAssignmentToTheConstant()
    {
        // ConstDeclNode is not an AssignmentNode, but a rule written by matching on `=` would report
        // the declaration that created the name.
        Assert.DoesNotContain(
            Lint("    const MAX = 4;"), d => d.Code == GscDiagnosticCode.CannotAssignToConstant);
    }
}
