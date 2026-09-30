using GSCode.Parser;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// <see cref="FormatOptions.FixCasing"/> on the formatter's side: keywords lowercase, calls take the
/// spelling the lookup gives, and nothing the preprocessor reads case-sensitively is touched. The
/// lookup is a fixed table here; <see cref="CallCasingTests"/> covers the workspace's answers.
/// </summary>
public class FixCasingTests
{
    private static readonly FormatOptions s_fixing = FormatOptions.Default with { UseTabs = true, FixCasing = true };

    private static readonly Dictionary<string, string> s_declared = new(StringComparer.OrdinalIgnoreCase)
    {
        ["foo"] = "foo",
        ["getplayers"] = "GetPlayers",
        ["wait_network_frame"] = "wait_network_frame",
        ["scale"] = "scale",
        ["shout"] = "SHOUT",
    };

    private static string? Lookup(string? qualifier, string name)
    {
        return s_declared.TryGetValue(name, out string? spelling) ? spelling : null;
    }

    private static string Format(string source, FormatOptions options)
    {
        ParseResult result = TestParse.Analyze(source);

        return GscFormatter.Format(result, options, Lookup) ?? throw new InvalidOperationException("formatter refused the input");
    }

    private static string Body(string statements)
    {
        string formatted = Format("function f()\n{\n" + statements + "\n}\n", s_fixing);

        return formatted["function f()\n{\n".Length..^"}\n".Length];
    }

    [Theory]
    [InlineData("If ( IsDefined( a ) )\nb = UNDEFINED;", "\tif ( isdefined( a ) )\n\t\tb = undefined;\n")]
    [InlineData("WAIT 0.05;", "\twait 0.05;\n")]
    [InlineData("Return TRUE;", "\treturn true;\n")]
    public void KeywordsAreLowercased(string source, string expected)
    {
        Assert.Equal(expected, Body(source));
    }

    [Theory]
    [InlineData("FOo();", "\tfoo();\n")]
    [InlineData("p = getplayers();", "\tp = GetPlayers();\n")]
    [InlineData("self thread FOO();", "\tself thread foo();\n")]
    [InlineData("util::Wait_Network_Frame();", "\tutil::wait_network_frame();\n")]
    [InlineData("ptr = &FOO;", "\tptr = &foo;\n")]
    [InlineData("ptr = &util::FOO;", "\tptr = &util::foo;\n")]
    public void CallsAndReferencesTakeTheLookupsSpelling(string source, string expected)
    {
        Assert.Equal(expected, Body(source));
    }

    [Fact]
    public void ADeclarationKeepsItsOwnSpelling()
    {
        Assert.StartsWith("function private FOo()\n", Format("function private FOo()\n{\n}\n", s_fixing), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownCallIsLeftAsWritten()
    {
        Assert.Equal("\tNotDeclared();\n", Body("NotDeclared();"));
    }

    [Fact]
    public void AMacroInvocationIsNeverRecased()
    {
        // `scale` is declared, but `SCALE( 2 )` spells the macro exactly, and macros are
        // case-sensitive: recasing it would make it a call to the function instead.
        string formatted = Format("#define SCALE( _v ) ( _v * 2 )\n\nfunction f()\n{\n\tx = SCALE( 2 );\n}\n", s_fixing);

        Assert.Contains("\tx = SCALE( 2 );\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void NoFixMayProduceAMacroName()
    {
        // The lookup says `shout` is spelled `SHOUT`, but that is a macro's name: writing it would
        // turn this call into the macro's expansion.
        string formatted = Format("#define SHOUT 1\n\nfunction f()\n{\n\tx = shout();\n}\n", s_fixing);

        Assert.Contains("\tx = shout();\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeywordShapedMacroKeepsItsCase()
    {
        string formatted = Format("#define DEFAULT 1\n\nfunction f()\n{\n\tx = DEFAULT;\n}\n", s_fixing);

        Assert.Contains("\tx = DEFAULT;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AMacroBodyIsNeverRecased()
    {
        string formatted = Format("#define CHECK( _a ) IsDefined( _a ) && FOo()\n\nfunction f()\n{\n\tx = 1;\n}\n", s_fixing);

        Assert.StartsWith("#define CHECK( _a ) IsDefined( _a ) && FOo()\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsRecasedWhenTheSettingIsOff()
    {
        string formatted = Format("function f()\n{\n\tIf ( IsDefined( a ) )\n\t\tFOo();\n}\n", s_fixing with { FixCasing = false });

        Assert.Contains("\tIf ( IsDefined( a ) )\n\t\tFOo();\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FixingCasingIsIdempotent()
    {
        string once = Format("function f()\n{\nIf ( IsDefined( a ) )\nFOo();\np = getplayers();\n}\n", s_fixing);

        Assert.Equal(once, Format(once, s_fixing));
    }
}
