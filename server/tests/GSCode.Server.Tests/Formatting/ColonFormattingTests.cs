using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// A ':' does three jobs. A ternary's and a base class's are spaced both sides, like C#; a
/// <c>case</c> or <c>default</c> label's hugs the label and ends the line. Every colon used to be
/// treated as a label's, which wrote <c>a ? b: c</c> and <c>class Derived: Base</c>, and left
/// <c>case 0: case 1: x = 1;</c> on one line.
/// </summary>
public class ColonFormattingTests
{
    private static readonly FormatOptions s_tabs = FormatOptions.Default with { UseTabs = true };

    private static string Format(string source)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, s_tabs) ?? throw new InvalidOperationException("formatter refused the input");
    }

    [Fact]
    public void ATernaryColonIsSpacedBothSides()
    {
        Assert.Contains("\tx = a ? -1 : ( b );\n", Format("function f()\n{\nx=a?-1:(b);\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void ABaseClassColonIsSpacedBothSides()
    {
        Assert.StartsWith("class Derived : Base\n", Format("class Derived:Base\n{\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void StackedLabelsEachTakeALineAndTheStatementGoesBelow()
    {
        Assert.Equal(
            "function f()\n{\n\tswitch ( v )\n\t{\n\t\tcase 0:\n\t\tcase 1:\n\t\t\tx = 1;\n\t\t\tbreak;\n\t\tdefault:\n\t\t\tfoo();\n\t\t\tbreak;\n\t}\n}\n",
            Format("function f()\n{\nswitch ( v )\n{\ncase 0: case 1: x = 1;\nbreak;\ndefault: foo();\nbreak;\n}\n}\n"));
    }

    [Fact]
    public void ATrailingCommentStaysOnItsLabel()
    {
        Assert.Contains("\t\tcase \"b\": // note\n", Format("function f()\n{\nswitch ( v )\n{\ncase \"b\": // note\nbreak;\n}\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void ATernaryInsideACaseBodyIsStillSpaced()
    {
        string formatted = Format("function f()\n{\nswitch ( v )\n{\ncase 0:\nx = a ? b : c;\nbreak;\n}\n}\n");

        Assert.Contains("\t\t\tx = a ? b : c;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ColonFormattingIsIdempotent()
    {
        string once = Format("class D:B\n{\n}\nfunction f()\n{\nswitch ( v )\n{\ncase 0: case 1: x = a?b:c;\nbreak;\n}\n}\n");

        Assert.Equal(once, Format(once));
    }
}
