using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// The two indentation choices stock never settled: whether <c>case</c> labels sit inside their
/// <c>switch</c>, and whether a dev block's body is indented.
/// </summary>
public class IndentOptionsTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private const string Switch =
        "function f()\n{\nswitch ( v )\n{\ncase 0:\na();\nbreak;\ndefault:\nb();\nbreak;\n}\n}\n";

    private const string DevBlock = "function f()\n{\n/#\nif ( a )\n{\nb();\n}\n#/\n}\n";

    private static string Format(string source, FormatOptions options)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, options) ?? throw new InvalidOperationException("formatter refused the input");
    }

    [Fact]
    public void CaseLabelsAreIndentedByDefault()
    {
        Assert.Equal(
            "function f()\n{\n\tswitch ( v )\n\t{\n\t\tcase 0:\n\t\t\ta();\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tb();\n\t\t\tbreak;\n\t}\n}\n",
            Format(Switch, s_tabs));
    }

    [Fact]
    public void FlushCaseLabelsSitInTheSwitchColumn()
    {
        Assert.Equal(
            "function f()\n{\n\tswitch ( v )\n\t{\n\tcase 0:\n\t\ta();\n\t\tbreak;\n\tdefault:\n\t\tb();\n\t\tbreak;\n\t}\n}\n",
            Format(Switch, s_tabs with { IndentCaseLabels = false }));
    }

    [Fact]
    public void FlushCaseLabelsNestInsideACaseBody()
    {
        string source = "function f()\n{\nswitch ( v )\n{\ncase 0:\nswitch ( w )\n{\ncase 1:\na();\nbreak;\n}\nbreak;\n}\n}\n";

        Assert.Equal(
            "function f()\n{\n\tswitch ( v )\n\t{\n\tcase 0:\n\t\tswitch ( w )\n\t\t{\n\t\tcase 1:\n\t\t\ta();\n\t\t\tbreak;\n\t\t}\n\t\tbreak;\n\t}\n}\n",
            Format(source, s_tabs with { IndentCaseLabels = false }));
    }

    [Fact]
    public void ADevBlockBodyStaysFlushByDefault()
    {
        Assert.Equal(
            "function f()\n{\n\t/#\n\tif ( a )\n\t{\n\t\tb();\n\t}\n\t#/\n}\n",
            Format(DevBlock, s_tabs));
    }

    [Fact]
    public void AnIndentedDevBlockIndentsItsBodyAndNotItsDelimiters()
    {
        Assert.Equal(
            "function f()\n{\n\t/#\n\t\tif ( a )\n\t\t{\n\t\t\tb();\n\t\t}\n\t#/\n}\n",
            Format(DevBlock, s_tabs with { IndentDevBlocks = true }));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothChoicesAreIdempotent(bool indentCaseLabels, bool indentDevBlocks)
    {
        FormatOptions options = s_tabs with { IndentCaseLabels = indentCaseLabels, IndentDevBlocks = indentDevBlocks };
        string source = "function f()\n{\n/#\nswitch ( v )\n{\ncase 0:\na();\nbreak;\n}\n#/\n}\n";

        string once = Format(source, options);

        Assert.Equal(once, Format(once, options));
    }
}
