using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// Nesting has a ceiling, because the alternative is a StackOverflowException — the one .NET
/// failure that cannot be caught and takes the whole language server process with it, every open
/// document's state included, with no diagnostic to explain why.
///
/// Every case here was measured to kill the process before the ceiling existed, on a thread with
/// the platform default 1 MB stack (what the server's thread-pool threads get):
///
///   x = ((((…            died between 470 and 500 levels — the parser's own descent, 8 frames
///                        per '(' (ParseExpression … ParsePrimary)
///   x = 1 + 1 + 1 + …    died between 1,000 and 2,000 terms, 1,426 frames of
///                        SymbolExtractor.WalkExpression — a chain the PARSER builds with a loop,
///                        so it is only the walker that recurses
///   x = a.b.b.b…         the same shape through MemberNode, the same range
///   #if ((((…            died between 1,000 and 3,000 levels — ConditionalEvaluator, a second
///                        recursive descent that the AST ceiling does not reach
///
/// The chain shapes are why the ceiling counts TREE levels rather than parser frames: they cost the
/// parser nothing and cost every walker over the result one frame per link.
/// </summary>
public class ParserDepthTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Analyses under a timeout, on a thread with the default stack.</summary>
    private static ParseResult AnalyzeWithinBudget(string source)
    {
        Task<ParseResult> parse = Task.Run(() => ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable()));

        Assert.True(
            parse.Wait(Budget),
            $"analysis did not finish within {Budget.TotalSeconds}s on this input");

        return parse.Result;
    }

    private static string InFunction(string body)
    {
        return "function f()\n{\n\t" + body + "\n}\n";
    }

    [Theory]
    // Parser recursion: each level is a fresh descent through the expression grammar.
    [InlineData("nested parentheses")]
    [InlineData("nested unary operators")]
    [InlineData("nested blocks")]
    [InlineData("nested ternaries")]
    // Tree depth only: the parser builds these with a loop, the walkers recurse over them.
    [InlineData("nested indexes")]
    [InlineData("binary chain")]
    [InlineData("member chain")]
    public void PathologicalNestingIsReportedAndSurvived(string shape)
    {
        // Every count here is comfortably past the measured overflow point for its shape, so a
        // regression is a dead test process rather than a red test — which is itself the signal.
        string source = shape switch
        {
            "nested parentheses" => InFunction("x = " + new string('(', 3000) + "1" + new string(')', 3000) + ";"),
            "nested unary operators" => InFunction("x = " + new string('!', 5000) + "1;"),
            "nested blocks" => InFunction(new string('{', 5000) + new string('}', 5000)),
            "nested ternaries" => InFunction("x = " + Repeat("a ? b : ", 3000) + "c;"),
            "nested indexes" => InFunction("x = a" + Repeat("[0]", 5000) + ";"),
            "binary chain" => InFunction("x = 1" + Repeat(" + 1", 5000) + ";"),
            "member chain" => InFunction("x = a" + Repeat(".b", 5000) + ";"),
            _ => throw new ArgumentException("unknown shape", nameof(shape)),
        };

        ParseResult result = AnalyzeWithinBudget(source);

        Assert.Contains(result.AllDiagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.NestingTooDeep);

        // Bounded, not merely finite: the point of abandoning the construct is that the unwind does
        // not report a second failure at every level it passes on the way out.
        Assert.InRange(result.AllDiagnostics.Length, 1, 500);
    }

    [Fact]
    public void ADeeplyParenthesisedConditionalDirectiveIsUnresolvableRatherThanFatal()
    {
        // ConditionalEvaluator is a second recursive descent, over the #if condition's tokens, and
        // the AST ceiling does not reach it. Too deep is simply a condition it cannot resolve —
        // the same answer it already gives for an unexpanded macro — so the branch goes inactive.
        ParseResult result = AnalyzeWithinBudget(
            "#if " + new string('(', 3000) + "1" + new string(')', 3000) + "\nfunction g() {}\n#endif\n");

        Assert.DoesNotContain(result.AllDiagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void AWalkerOverTheDeepestTreeTheCeilingAllowsSurvives()
    {
        // SymbolExtractor is covered above (it is what the chain shapes used to overflow).
        // AstPrinter is the other walker in this project and the deepest-recursing one — a node per
        // frame, no loops — and it is the one that showed capping the TREE does not by itself make
        // every consumer safe: the cap is counted in grammar entries, and survival is counted in
        // bytes of frame. At the cap this printer cleared a 1 MB stack in Release and died at 242
        // levels in Debug, taking the whole test host with it. It now carries its own ceiling.
        //
        // Note what this asserts and what it does not: the walker RETURNS, and returns something
        // well-formed. Past AstPrinter.MaxPrintDepth the subtree is a marker, which is the intended
        // answer for a tree this deep — the same input is already reported as NestingTooDeep.
        ParseTree tree = ParserTestHelper.Parse(InFunction("x = a" + Repeat(".b", 5000) + ";"));

        Assert.Contains("(. ", AstPrinter.Print(tree.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrintersOwnCeilingTruncatesRatherThanGrowingWithTheTree()
    {
        // Pins the guard, not just the survival: without it the printer's output tracks the tree's
        // full depth, which is what a recursion-per-level walker has no budget for. Two chains an
        // order of magnitude apart in length must print the same, because both are cut at the same
        // ceiling — a length that still varied would mean the ceiling was not what stopped it.
        string shallower = AstPrinter.Print(ParserTestHelper.Parse(InFunction("x = a" + Repeat(".b", 500) + ";")).Root);
        string deeper = AstPrinter.Print(ParserTestHelper.Parse(InFunction("x = a" + Repeat(".b", 5000) + ";")).Root);

        Assert.Equal(shallower, deeper);
        Assert.Contains("(...)", deeper, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryCodeNeverReachesThePrintersCeiling()
    {
        // The other half of the bar: a truncation marker in the golden format of hand-written code
        // would be a silent wrong answer, since every parser test in this project reads that format.
        string printed = ParserTestHelper.PrintScript(
            InFunction("x = a.b.c.d; y = f( 1 + 2 * 3, g( h( 4 ) ) ); if ( x ) { switch ( y ) { case 1: break; } }"));

        Assert.DoesNotContain("(...)", printed, StringComparison.Ordinal);
    }

    [Theory]
    // A hundred levels of everything the ceiling counts. Nothing hand-written comes close, so a
    // ceiling that reported here would be reporting on code that works.
    [InlineData("x = ((((1))));")]
    [InlineData("x = !!!!1;")]
    [InlineData("x = a ? b : c ? d : e;")]
    [InlineData("x = a[0][0][0];")]
    [InlineData("x = 1 + 1 + 1 + 1;")]
    [InlineData("x = a.b.c.d;")]
    public void OrdinaryNestingIsUntouched(string statement)
    {
        Assert.Empty(AnalyzeWithinBudget(InFunction(statement)).AllDiagnostics);
    }

    [Theory]
    [InlineData("nested parentheses")]
    [InlineData("nested unary operators")]
    [InlineData("nested blocks")]
    [InlineData("binary chain")]
    [InlineData("member chain")]
    public void ADepthOfOneHundredStillParsesCleanly(string shape)
    {
        string source = shape switch
        {
            "nested parentheses" => InFunction("x = " + new string('(', 100) + "1" + new string(')', 100) + ";"),
            "nested unary operators" => InFunction("x = " + new string('!', 100) + "1;"),
            "nested blocks" => InFunction(new string('{', 100) + new string('}', 100)),
            "binary chain" => InFunction("x = 1" + Repeat(" + 1", 100) + ";"),
            "member chain" => InFunction("x = a" + Repeat(".b", 100) + ";"),
            _ => throw new ArgumentException("unknown shape", nameof(shape)),
        };

        Assert.Empty(AnalyzeWithinBudget(source).AllDiagnostics);
    }

    private static string Repeat(string unit, int count)
    {
        return string.Concat(Enumerable.Repeat(unit, count));
    }
}
