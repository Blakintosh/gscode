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
/// A threaded call hands back control at the callee's first `wait`, not at its `return`, so its
/// value is `undefined` for any function that waits. Reading that value is the finding.
///
/// The rule is positional and needs no types: an expression STATEMENT discards its value, which is
/// what `thread foo();` is for, and every other position wants one. Most of what is below is the
/// boundary between those two, since getting it wrong in the generous direction reports the
/// ordinary form on every threaded call in the codebase.
/// </summary>
public class ThreadedResultLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        string source = "function f( a, b )\n{\n" + body + "\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return ThreadedResultLint.Analyze(result);
    }

    [Fact]
    public void AssigningAThreadedCallIsReported()
    {
        // The control, and the shape 1.5 had a second code for.
        Diagnostic reported = Assert.Single(Lint("    x = thread build();"));

        Assert.Equal(GscDiagnosticCode.ConsumedThreadedCallResult, reported.Code);
        // The message has to say WHEN it breaks. A threaded function with no wait returns the right
        // value today, so "this is undefined" alone invites the reader to check once and dismiss it.
        Assert.Contains("first 'wait'", reported.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("    thread build();")]
    [InlineData("    self thread build();")]
    [InlineData("    a thread build( 1, 2 );")]
    [InlineData("    ( thread build() );")]
    public void AThreadedCallAsAStatementIsTheOrdinaryForm(string body)
    {
        Assert.Empty(Lint(body));
    }

    [Theory]
    [InlineData("    if ( thread ready() ) { return; }")]
    [InlineData("    return thread build();")]
    [InlineData("    wait thread duration();")]
    [InlineData("    spawn( thread build() );")]
    [InlineData("    x = a + thread build();")]
    [InlineData("    x = thread build()[ 0 ];")]
    [InlineData("    x = ( thread build() );")]
    [InlineData("    while ( thread ready() ) { break; }")]
    [InlineData("    switch ( thread pick() ) { default: break; }")]
    [InlineData("    foreach ( v in thread list() ) { break; }")]
    public void EveryOtherPositionConsumesTheValue(string body)
    {
        Assert.Single(Lint(body), d => d.Code == GscDiagnosticCode.ConsumedThreadedCallResult);
    }

    [Fact]
    public void AnUnthreadedCallIsNeverReported()
    {
        // The rule is about `thread`, not about calls. Without this the whole codebase lights up.
        Assert.Empty(Lint("    x = build();\n    if ( ready() ) { return; }"));
    }

    [Fact]
    public void AThreadedArgumentToAThreadedStatementIsStillReported()
    {
        // The outer call starts a thread properly and discards its value; the inner one's value is
        // read as an argument. Only the inner is the mistake.
        Diagnostic reported = Assert.Single(Lint("    thread spawn( thread build() );"));

        Assert.Equal(GscDiagnosticCode.ConsumedThreadedCallResult, reported.Code);
        // Reported on the inner call, so the squiggle sits on the argument rather than the statement.
        Assert.True(reported.Range.Start.Character > "    thread spawn( ".Length - 1);
    }

    [Fact]
    public void AThreadedCallInsideAForIncrementIsAStatement()
    {
        // The parser wraps a for-loop's initializer and increment in ExprStatementNode, so they are
        // statement position too — the value is discarded there exactly as it is at the top level.
        Assert.Empty(Lint("    for ( i = 0; i < 3; thread tick() ) { }"));
    }

    [Fact]
    public void AThreadedCallInAForConditionIsReported()
    {
        // Same statement, different slot: a condition is tested, so it consumes.
        Assert.Single(
            Lint("    for ( i = 0; thread ready(); i++ ) { }"),
            d => d.Code == GscDiagnosticCode.ConsumedThreadedCallResult);
    }

    [Fact]
    public void ACompoundAssignmentCountsAsConsuming()
    {
        // `x += thread build()` reads the value to add it. The operator does not change the answer.
        Assert.Single(Lint("    x += thread build();"), d => d.Code == GscDiagnosticCode.ConsumedThreadedCallResult);
    }

    [Fact]
    public void AThreadedCallDeepInsideAStatementBodyIsFound()
    {
        // The walk has to keep descending through nested statements, not stop at the first block.
        Assert.Single(
            Lint("    if ( a )\n    {\n        foreach ( v in b )\n        {\n            x = thread build();\n        }\n    }"),
            d => d.Code == GscDiagnosticCode.ConsumedThreadedCallResult);
    }
}
