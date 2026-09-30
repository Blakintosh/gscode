using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A closed block is followed by a blank line before the next statement, so consecutive loops do
/// not run into each other. Stock puts one there 15,940 times against 3,012. What continues the
/// same construct — <c>else</c>, a do-while's tail, the next label, a closer — still sits directly
/// under the <c>}</c>.
/// </summary>
public class BlankLineAfterBlockTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Format(string source, FormatOptions options)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, options) ?? throw new InvalidOperationException("formatter refused the input");
    }

    [Fact]
    public void ConsecutiveLoopsAreSeparatedByABlankLine()
    {
        string source = "function f()\n{\nfor ( ;; )\n{\nbreak;\n}\nforeach ( v in a )\n{\ng();\n}\ndo\n{\ni--;\n}\nwhile ( i > 0 );\nh();\n}\n";

        Assert.Equal(
            "function f()\n{\n\tfor ( ;; )\n\t{\n\t\tbreak;\n\t}\n\n\tforeach ( v in a )\n\t{\n\t\tg();\n\t}\n\n\tdo\n\t{\n\t\ti--;\n\t}\n\twhile ( i > 0 );\n\n\th();\n}\n",
            Format(source, s_tabs));
    }

    [Fact]
    public void AnElseChainStaysTogether()
    {
        string source = "function f()\n{\nif ( a )\n{\nb();\n}\nelse if ( c )\n{\nd();\n}\nelse\n{\ne();\n}\n}\n";

        Assert.DoesNotContain("}\n\n\telse", Format(source, s_tabs), StringComparison.Ordinal);
    }

    [Fact]
    public void ABreakAfterABracedCaseBodyHugsIt()
    {
        string source = "function f()\n{\nswitch ( v )\n{\ncase 0:\n{\na();\n}\nbreak;\ncase 1:\nbreak;\n}\n}\n";

        Assert.Contains("\t\t\t}\n\t\t\tbreak;\n\t\tcase 1:", Format(source, s_tabs), StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailingCommentOnTheBraceKeepsTheBlankForTheNextLine()
    {
        string source = "function f()\n{\nif ( a )\n{\nb();\n} // done\nc();\n}\n";

        Assert.Contains("\t} // done\n\n\tc();", Format(source, s_tabs), StringComparison.Ordinal);
    }

    [Fact]
    public void MaxBlankLinesOfZeroStillWins()
    {
        string source = "function f()\n{\nif ( a )\n{\nb();\n}\nc();\n}\n";

        Assert.Contains("\t}\n\tc();", Format(source, s_tabs with { MaxBlankLines = 0 }), StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedUnbracedChainGetsOneBlankLineAfterItsStatement()
    {
        // Nested unbraced bodies are legal and stay as written; the one ';' ends the whole chain.
        Assert.Equal(
            "function f()\n{\n\tif ( a )\n\t\tif ( b )\n\t\t\tc();\n\n\td();\n}\n",
            Format("function f()\n{\nif ( a )\nif ( b )\nc();\nd();\n}\n", s_tabs));
    }

    [Fact]
    public void AnUnbracedIfElseStaysTogether()
    {
        Assert.Equal(
            "function f()\n{\n\tif ( x )\n\t\ty();\n\telse\n\t\tz();\n\n\tw();\n}\n",
            Format("function f()\n{\nif ( x )\ny();\nelse\nz();\nw();\n}\n", s_tabs));
    }

    [Fact]
    public void AnUnbracedDoBodyKeepsItsTailAndTheBlankGoesAfter()
    {
        Assert.Equal(
            "function f()\n{\n\tdo\n\t\ti--;\n\twhile ( i > 0 );\n\n\th();\n}\n",
            Format("function f()\n{\ndo\ni--;\nwhile ( i > 0 );\nh();\n}\n", s_tabs));
    }

    [Fact]
    public void AnUnbracedForBodyEndsAtItsStatementNotItsHeader()
    {
        Assert.Equal(
            "function f()\n{\n\tfor ( i = 0; i < 3; i++ )\n\t\tg( i );\n\n\th();\n}\n",
            Format("function f()\n{\nfor ( i = 0; i < 3; i++ )\ng( i );\nh();\n}\n", s_tabs));
    }

    [Fact]
    public void FunctionsAreSeparatedByABlankLine()
    {
        Assert.Equal(
            "function f()\n{\n}\n\nfunction g()\n{\n}\n",
            Format("function f()\n{\n}\nfunction g()\n{\n}\n", s_tabs));
    }
}
