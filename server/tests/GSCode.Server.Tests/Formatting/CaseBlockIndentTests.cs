using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// `IndentCaseBlocks` decides where a case body that is one braced block sits. On (the default),
/// the '{' is indented inside its label like any other case body, so the statements land two
/// levels in from `case`. Off, the '{' is level with the label and the statements one level in.
/// Reported as "tabbing out twice" for case blocks.
/// </summary>
public class CaseBlockIndentTests
{
    private const string Input = """
        function f( a )
        {
        	switch ( a )
        	{
        		case 1:
        		{
        			b = 1;
        			break;
        		}
        		case 2:
        			b = 2;
        			break;
        		default:
        		{
        			switch ( b )
        			{
        				case 3:
        				{
        					c = 3;
        				}
        			}
        		}
        	}
        	d = a ? 1 : 2;
        }

        """;

    private static string Format(FormatOptions options)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(Input.ReplaceLineEndings("\n")),
            NullInsertProvider.Instance,
            new NameTable());

        string? formatted = GscFormatter.Format(result, options with { UseTabs = true });
        Assert.NotNull(formatted);
        return formatted;
    }

    [Fact]
    public void ABracedCaseBodyIsIndentedInsideItsLabelByDefault()
    {
        string formatted = Format(FormatOptions.Default);

        Assert.Contains("\t\tcase 1:\n\t\t\t{\n\t\t\t\tb = 1;\n\t\t\t\tbreak;\n\t\t\t}\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheOptionOffTheBraceSitsLevelWithTheLabel()
    {
        string formatted = Format(FormatOptions.Default with { IndentCaseBlocks = false });

        Assert.Contains("\t\tcase 1:\n\t\t{\n\t\t\tb = 1;\n\t\t\tbreak;\n\t\t}\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ABareCaseBodyIsIndentedEitherWay()
    {
        string formatted = Format(FormatOptions.Default with { IndentCaseBlocks = false });

        Assert.Contains("\t\tcase 2:\n\t\t\tb = 2;\n\t\t\tbreak;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedSwitchInsideACaseBlockFollowsTheSameRule()
    {
        string formatted = Format(FormatOptions.Default with { IndentCaseBlocks = false });

        Assert.Contains(
            "\t\tdefault:\n\t\t{\n\t\t\tswitch ( b )\n\t\t\t{\n\t\t\t\tcase 3:\n\t\t\t\t{\n\t\t\t\t\tc = 3;\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n",
            formatted,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ATernaryColonIsNotMistakenForALabel()
    {
        // The label test is "a ':' with no '?' waiting", so the ternary after the switch must not
        // mark a following '{' as a case block.
        string formatted = Format(FormatOptions.Default with { IndentCaseBlocks = false });

        Assert.Contains("\td = a ? 1 : 2;\n}", formatted, StringComparison.Ordinal);
    }
}
