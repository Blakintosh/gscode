using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// A loop's own induction variable is still an assignment — it should be completed and typed like
/// any other local — but it is not a symbol anyone navigates to. Every `for` and `foreach` in a
/// file contributes an `i`, `key` or `value`, which is what made the outline look like it was
/// listing the loops themselves.
/// </summary>
public class LoopVariableTests
{
    private static ImmutableArray<AssignmentSymbol> Assignments(string body)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\n" + body + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return result.Extraction.Functions.Single().Assignments;
    }

    private static AssignmentSymbol Named(string body, string name)
    {
        return Assignments(body).First(a => string.Equals(a.Name, name, StringComparison.Ordinal));
    }

    [Fact]
    public void AForCounterIsALoopVariable()
    {
        Assert.True(Named("    for ( i = 0; i < 10; i++ )\n    {\n    }", "i").IsLoopVariable);
    }

    [Fact]
    public void ForeachKeyAndValueAreLoopVariables()
    {
        const string body = "    foreach ( key, value in things )\n    {\n    }";

        Assert.True(Named(body, "key").IsLoopVariable);
        Assert.True(Named(body, "value").IsLoopVariable);
    }

    [Fact]
    public void AForeachValueAloneIsALoopVariable()
    {
        Assert.True(Named("    foreach ( player in players )\n    {\n    }", "player").IsLoopVariable);
    }

    [Fact]
    public void AnOrdinaryLocalIsNot()
    {
        Assert.False(Named("    total = 0;", "total").IsLoopVariable);
    }

    [Fact]
    public void AnAssignmentInsideTheLoopBodyIsNot()
    {
        // Only the loop's OWN variable is excluded; real work inside the body still counts.
        const string body = "    foreach ( player in players )\n    {\n        best_score = 10;\n    }";

        Assert.False(Named(body, "best_score").IsLoopVariable);
        Assert.True(Named(body, "player").IsLoopVariable);
    }

    [Fact]
    public void AnAssignmentAfterTheLoopIsNot()
    {
        // The `for` initializer is marked by rewriting the entries it added, so the boundary of
        // that range matters: anything appended later must be untouched.
        const string body = "    for ( i = 0; i < 3; i++ )\n    {\n    }\n    done = true;";

        Assert.True(Named(body, "i").IsLoopVariable);
        Assert.False(Named(body, "done").IsLoopVariable);
    }

    [Fact]
    public void LoopVariablesAreStillRecorded()
    {
        // Excluded from the outline, not from the model: completion and type flow still need them.
        Assert.Contains(Assignments("    foreach ( player in players )\n    {\n    }"),
            a => string.Equals(a.Name, "player", StringComparison.Ordinal));
    }
}
