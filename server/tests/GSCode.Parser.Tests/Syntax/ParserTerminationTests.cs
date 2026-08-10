using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// The parser must always terminate, on any input.
///
/// It runs on every keystroke against text the user is midway through typing, so malformed input
/// is its NORMAL diet rather than an edge case. A statement parser that consumes nothing leaves
/// the enclosing loop on the same token and it calls straight back in — and because each pass
/// appends a diagnostic, the failure is not a hang but unbounded memory growth. The reported case
/// reached 23.6 GB.
///
/// Every case here is run under a timeout, so a regression fails the suite instead of taking the
/// machine down with it.
///
/// Terminating is only half of it: input can also be too DEEP rather than non-terminating, which
/// fails as a stack overflow instead of as memory growth. That half is <see cref="ParserDepthTests"/>.
/// </summary>
public class ParserTerminationTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    /// <summary>Analyses under a timeout, returning the diagnostic count as proof it finished.</summary>
    private static int AnalyzeWithinBudget(string source)
    {
        Task<int> parse = Task.Run(() =>
        {
            ParseResult result = ScriptAnalysis.Analyze(
                @"c:\ws\scripts\t.gsc",
                ScriptLanguage.Gsc,
                SourceText.From(source),
                NullInsertProvider.Instance,
                new NameTable());

            return result.AllDiagnostics.Length;
        });

        Assert.True(
            parse.Wait(Budget),
            $"the parser did not finish within {Budget.TotalSeconds}s — it is not terminating on this input");

        return parse.Result;
    }

    [Fact]
    public void ABareFunctionKeywordFollowedByARealDeclaration()
    {
        // The exact reported document: a half-typed `function` left on its own line above a
        // complete one, which is simply what a file looks like partway through writing a function.
        //
        // `function` is BOTH a recovery sync point and unable to start an expression, so
        // ParseExpressionStatement reported an error and recovered to a token it was already on.
        int diagnostics = AnalyzeWithinBudget(
            "#namespace test;\n\nfunction foobar()\n{\n}\n\nfunction\n\nfunction foo()\n{\n}\n");

        // It should complain — the input is malformed — but a bounded number of times.
        Assert.InRange(diagnostics, 1, 100);
    }

    [Theory]
    // Every sync point RecoverToStatement stops at, sitting where a statement is expected. Each is
    // a token that cannot start an expression, so each reaches the same path.
    [InlineData("function f()\n{\n\tfunction\n}\n")]
    [InlineData("function f()\n{\n\tclass\n}\n")]
    [InlineData("function f()\n{\n\telse\n}\n")]
    [InlineData("function f()\n{\n\tcase\n}\n")]
    [InlineData("function f()\n{\n\t)\n}\n")]
    [InlineData("function f()\n{\n\t]\n}\n")]
    [InlineData("function f()\n{\n\t,\n}\n")]
    [InlineData("function f()\n{\n\t:\n}\n")]
    public void ATokenThatCannotStartAStatement(string source)
    {
        AnalyzeWithinBudget(source);
    }

    [Fact]
    public void ABareFunctionKeywordAtTopLevel()
    {
        AnalyzeWithinBudget("function\n");
    }

    [Fact]
    public void AClassBodyFullOfNonsense()
    {
        AnalyzeWithinBudget("class C\n{\n\t)\n\t,\n\tfunction\n}\n");
    }

    [Fact]
    public void TheDiagnosticCountStaysBoundedOnRepeatedGarbage()
    {
        // The failure mode was one diagnostic per pass of a loop that never advanced, so a count
        // wildly out of proportion to the input is the symptom even when it does terminate.
        int diagnostics = AnalyzeWithinBudget(
            string.Concat(Enumerable.Repeat("function f()\n{\n\tfunction\n}\n", 20)));

        Assert.InRange(diagnostics, 1, 500);
    }

    [Fact]
    public void WellFormedCodeIsUnaffected()
    {
        Assert.Equal(0, AnalyzeWithinBudget(
            "#namespace test;\n\nfunction foo()\n{\n\tx = 1;\n\treturn x;\n}\n"));
    }

    // --- Every other loop bounded only by a closing token ---
    //
    // The statement loop was the one that failed, and it failed because a recovery helper could
    // return without advancing. Each loop below has the same shape — `while` stopping only at a
    // closer or end of file, with a default branch that recovers — so each is asked the same
    // question directly rather than by reading its recovery helper and concluding it looks fine.

    [Theory]
    [InlineData(")")]
    [InlineData("]")]
    [InlineData(",")]
    [InlineData(":")]
    [InlineData("=")]
    [InlineData("else")]
    [InlineData("case")]
    [InlineData("#endif")]
    public void GarbageAtTopLevel(string garbage)
    {
        AnalyzeWithinBudget(garbage + "\n");
    }

    [Theory]
    [InlineData(")")]
    [InlineData(",")]
    [InlineData("else")]
    [InlineData("function")]
    public void GarbageInAClassBody(string garbage)
    {
        AnalyzeWithinBudget("class C\n{\n\t" + garbage + "\n}\n");
    }

    [Theory]
    [InlineData(")")]
    [InlineData(",")]
    [InlineData("else")]
    public void GarbageInADevBlock(string garbage)
    {
        AnalyzeWithinBudget("/#\n" + garbage + "\n#/\n");
    }

    [Theory]
    [InlineData("function f( , ) {}")]
    [InlineData("function f( ) ) {}")]
    [InlineData("function f( else ) {}")]
    [InlineData("function f( a b c ) {}")]
    public void GarbageInAParameterList(string source)
    {
        AnalyzeWithinBudget(source + "\n");
    }

    [Theory]
    [InlineData("g( , );")]
    [InlineData("g( else );")]
    [InlineData("g( a b );")]
    [InlineData("x = [ , ];")]
    public void GarbageInAnArgumentList(string statement)
    {
        AnalyzeWithinBudget("function f()\n{\n\t" + statement + "\n}\n");
    }

    [Theory]
    [InlineData("switch ( x )\n{\n\t)\n}")]
    [InlineData("switch ( x )\n{\n\tcase\n}")]
    [InlineData("switch ( x )\n{\n\tcase 1:\n\t\t)\n}")]
    public void GarbageInASwitch(string statement)
    {
        AnalyzeWithinBudget("function f()\n{\n" + statement + "\n}\n");
    }

    [Theory]
    [InlineData("function f()\n{")]
    [InlineData("class C\n{")]
    [InlineData("/#\nfunction f()\n{\n}")]
    [InlineData("function f( ")]
    [InlineData("function f()\n{\n\tg( ")]
    [InlineData("switch ( x )\n{\n\tcase 1:")]
    public void UnterminatedConstructs(string source)
    {
        // Everything above ends mid-construct, which is what a file looks like while it is being
        // written — the parser's normal input, not an edge case.
        AnalyzeWithinBudget(source);
    }
}
