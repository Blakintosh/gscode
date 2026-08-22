using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A control-flow body written without braces opens no brace, so indentation derived from brace
/// depth alone put it in the header's own column. These pin the corrected indent.
/// </summary>
public class UnbracedBodyFormattingTests
{
    private static string Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result) ?? throw new InvalidOperationException("formatter refused the input");
    }

    private static string LineWith(string formatted, string needle)
    {
        return formatted.Split('\n').First(line => line.Contains(needle, StringComparison.Ordinal));
    }

    private static int IndentOf(string line)
    {
        return line.Length - line.TrimStart(' ').Length;
    }

    [Fact]
    public void UnbracedIfBody_IsIndentedPastTheHeader()
    {
        string formatted = Format("function f()\n{\nif ( a )\ndoThing();\n}\n");

        int header = IndentOf(LineWith(formatted, "if ("));
        int body = IndentOf(LineWith(formatted, "doThing"));

        Assert.True(body > header, $"body should sit deeper than the header:\n{formatted}");
    }

    [Fact]
    public void StatementAfterTheBody_ReturnsToHeaderIndent()
    {
        // The indent must be released, or everything below the `if` drifts right forever.
        string formatted = Format("function f()\n{\nif ( a )\ndoThing();\nafter();\n}\n");

        Assert.Equal(IndentOf(LineWith(formatted, "if (")), IndentOf(LineWith(formatted, "after")));
    }

    [Fact]
    public void NestedUnbracedBodies_StackAndReleaseTogether()
    {
        string formatted = Format("function f()\n{\nif ( a )\nif ( b )\ndoThing();\nafter();\n}\n");

        int outer = IndentOf(LineWith(formatted, "if ( a )"));
        int inner = IndentOf(LineWith(formatted, "if ( b )"));
        int body = IndentOf(LineWith(formatted, "doThing"));

        Assert.True(inner > outer && body > inner, $"each level should nest:\n{formatted}");
        Assert.Equal(outer, IndentOf(LineWith(formatted, "after")));
    }

    [Theory]
    [InlineData("while ( a )")]
    [InlineData("for ( i = 0; i < 3; i++ )")]
    [InlineData("foreach ( k in things )")]
    public void EveryLoopHeaderIndentsAnUnbracedBody(string header)
    {
        string formatted = Format($"function f()\n{{\n{header}\ndoThing();\n}}\n");

        Assert.True(IndentOf(LineWith(formatted, "doThing")) > IndentOf(LineWith(formatted, "(")));
    }

    [Fact]
    public void ElseWithoutBraces_IndentsItsBody()
    {
        string formatted = Format("function f()\n{\nif ( a )\ndoThing();\nelse\ndoOther();\n}\n");

        int elseIndent = IndentOf(LineWith(formatted, "else"));
        Assert.True(IndentOf(LineWith(formatted, "doOther")) > elseIndent, formatted);
    }

    [Fact]
    public void BracedBodies_AreUnchanged()
    {
        // Brace depth already handled these; the tracker must not double-indent them.
        string formatted = Format("function f()\n{\nif ( a )\n{\ndoThing();\n}\nafter();\n}\n");

        Assert.Equal(IndentOf(LineWith(formatted, "if (")), IndentOf(LineWith(formatted, "after")));
        Assert.True(IndentOf(LineWith(formatted, "doThing")) > IndentOf(LineWith(formatted, "if (")));
    }

    [Fact]
    public void SameLineBody_StaysOnItsLine()
    {
        string formatted = Format("function f()\n{\nif ( a ) return;\n}\n");

        Assert.Contains("if ( a ) return;", formatted, StringComparison.Ordinal);
    }
}
