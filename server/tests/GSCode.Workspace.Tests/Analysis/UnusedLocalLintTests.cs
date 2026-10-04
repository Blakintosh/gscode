using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// A local assigned and never read. Hint severity: dead code is worth knowing about but the script
/// still runs, and work in progress is the usual reason to have one. The Unnecessary tag is what
/// reports it — the editor greys the name — so it does not also fill the problem list, which over
/// the corpus it did 4,711 times in code that ships and works.
///
/// Reads and writes are told apart structurally, not by counting occurrences — which is the part
/// worth testing, since every way of reading a name has to be recognised or the lint reports
/// live code as dead.
/// </summary>
public class UnusedLocalLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        ParseResult result = TestParse.Analyze("function f( p )\n{\n" + body + "\n}\n");

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
        Assert.Equal(DiagnosticSeverity.Hint, unused.Severity);
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
    public void AClassMethodWritingAMemberIsNotReported()
    {
        // The reported false positive, reduced from BO3's scripts\shared\doors_shared.gsc: a class
        // method setting one of its own `var` members looks exactly like a dead store to a rule
        // that scopes names per body with no model of the class — the same reasoning that already
        // exempts a constructor exempts every method, since a bare name inside either may be a
        // member rather than a local.
        string source =
            "class cDoor\n{\n"
            + "    var m_n_door_connect_paths;\n\n"
            + "    function set_door_paths( n_door_connect_paths )\n"
            + "    {\n"
            + "        m_n_door_connect_paths = n_door_connect_paths;\n"
            + "    }\n"
            + "}\n";

        ParseResult result = TestParse.Analyze(source);

        Assert.Empty(UnusedLocalLint.Analyze(result));
    }

    [Fact]
    public void EachFunctionIsSeparate()
    {
        // A name read in another function does not keep this one alive.
        ParseResult result = TestParse.Analyze("function a()\n{\n    bar = 1;\n}\nfunction b()\n{\n    bar = 2;\n    use( bar );\n}\n");

        Assert.Single(UnusedLocalLint.Analyze(result));
    }
}
