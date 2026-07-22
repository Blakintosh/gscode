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
/// A local assigned and never read. Information severity: dead code is worth knowing about but
/// the script still runs, and work in progress is the usual reason to have one.
///
/// Reads and writes are told apart structurally, not by counting occurrences — which is the part
/// worth testing, since every way of reading a name has to be recognised or the lint reports
/// live code as dead.
/// </summary>
public class UnusedLocalLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f( p )\n{\n" + body + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return UnusedLocalLint.Analyze(result);
    }

    private static bool Reports(string body, string name)
    {
        return Lint(body).Any(d => d.Message.Contains($"'{name}'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAssignmentNeverReadIsReported()
    {
        // The reported shape.
        Diagnostic unused = Assert.Single(Lint("    bar = undefined;"));

        Assert.Equal(GscDiagnosticCode.UnusedLocal, unused.Code);
        Assert.Equal(DiagnosticSeverity.Information, unused.Severity);
        Assert.Contains("bar", unused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsTaggedUnnecessarySoTheEditorGreysIt()
    {
        Assert.Equal(DiagnosticTag.Unnecessary, Assert.Single(Assert.Single(Lint("    bar = 1;")).Tags));
    }

    // --- Every way of reading a name has to count ---

    [Theory]
    [InlineData("    bar = 1;\n    use( bar );")]                 // an argument
    [InlineData("    bar = 1;\n    x = bar + 1;")]                // an operand
    [InlineData("    bar = 1;\n    if ( bar ) { }")]              // a condition
    [InlineData("    bar = 1;\n    return bar;")]                 // returned
    [InlineData("    bar = 1;\n    bar.field = 2;")]              // a member's object
    [InlineData("    bar = 1;\n    x = bar[0];")]                 // an index's object
    [InlineData("    bar = 1;\n    bar thread helper();")]        // the object a method is called on
    [InlineData("    bar = 1;\n    x = [[ bar ]]();")]            // a function pointer being called
    [InlineData("    bar = 1;\n    foreach ( v in bar ) { }")]    // the collection
    [InlineData("    bar = 1;\n    switch ( bar ) { }")]          // a switch subject
    [InlineData("    bar = 1;\n    wait bar;")]                   // a wait duration
    [InlineData("    bar = 1;\n    x = ( bar );")]                // parenthesised
    [InlineData("    bar = 1;\n    x = cond ? bar : 0;")]         // a ternary arm
    public void AReadOfAnyKindSuppressesIt(string body)
    {
        Assert.False(Reports(body, "bar"));
    }

    [Theory]
    [InlineData("    bar = 1;\n    bar += 2;")]   // a compound assignment reads its target
    [InlineData("    bar = 1;\n    bar++;")]      // and so does ++
    public void AReadWriteIsNotADeadStore(string body)
    {
        Assert.False(Reports(body, "bar"));
    }

    // --- What must not be reported ---

    [Fact]
    public void AFieldWriteIsNotALocal()
    {
        // Another script may read self.foo, so an unread write here says nothing.
        Assert.Empty(Lint("    self.foo = 1;\n    level.bar = 2;"));
    }

    [Fact]
    public void AParameterIsNotADeadStore()
    {
        // The caller supplied it; an unread parameter is a different finding with a different rule.
        Assert.Empty(Lint("    x = 1;\n    use( x );"));
    }

    [Fact]
    public void ALoopVariableIsNotReported()
    {
        // `foreach ( key, value in … )` where only one is used is idiomatic, not dead.
        Assert.Empty(Lint("    foreach ( key, value in things )\n    {\n        use( value );\n    }"));
    }

    [Fact]
    public void ACalleeIsNotAReadOfALocalOfTheSameName()
    {
        // `helper()` calls a FUNCTION; it does not read a local called helper. Missing this would
        // silently suppress the report.
        Assert.True(Reports("    helper = 1;\n    helper();", "helper"));
    }

    [Fact]
    public void OnlyTheFirstWriteIsReported()
    {
        // A later write is dead only because the first one was; one diagnostic per name.
        Diagnostic unused = Assert.Single(Lint("    bar = 1;\n    bar = 2;"));

        Assert.Equal(2, unused.Range.Start.Line);
    }

    [Fact]
    public void EachFunctionIsSeparate()
    {
        // A name read in another function does not keep this one alive.
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function a()\n{\n    bar = 1;\n}\nfunction b()\n{\n    bar = 2;\n    use( bar );\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        Assert.Single(UnusedLocalLint.Analyze(result));
    }
}
