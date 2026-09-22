using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Tests.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// A function whose declaration is produced by a MACRO — REGISTER_SYSTEM in BO3's own shared.gsh
/// being the real case — should be reported at its INVOCATION site, not at the position the
/// `function` keyword and name happen to sit at inside the macro's own #define body.
///
/// The reported symptom: a CodeLens read "autoexec entry point" on an unrelated `else` line (the
/// macro's home in shared.gsh), because <see cref="FunctionSymbol.NameRange"/> was taken straight
/// from the name token's own <c>Range</c> — the header position — rather than its
/// <c>RootRange</c> — the invocation. <see cref="FunctionSymbol.FullRange"/> already used the
/// invocation (built from <c>RootRange</c> in the parser), so the two disagreed on which file and
/// line the declaration belonged to.
/// </summary>
public class MacroGeneratedDeclarationTests
{
    private const string GshPath = @"scripts\shared\shared.gsh";

    // Mirrors REGISTER_SYSTEM in the real shared.gsh: expands to an autoexec init function whose
    // entire declaration — keyword, name, body — is macro text, never written by the including
    // file at all.
    private const string RegisterSystemHeader =
        "#define REGISTER_SYSTEM(__sys,__reqs) \\\n" +
        "\tfunction autoexec __init__sytem__() { \\\n" +
        "\t\tsystem::register(__sys,undefined,undefined,__reqs); \\\n" +
        "\t}\n";

    private static ParseResult Analyze(string rootSource)
    {
        FakeInsertProvider provider = new FakeInsertProvider().AddInsert(GshPath, RegisterSystemHeader);

        return ScriptAnalysis.Analyze(
            PreprocessTestHelper.RootPath,
            ScriptLanguage.Gsc,
            SourceText.From(rootSource),
            provider,
            new NameTable());
    }

    [Fact]
    public void AutoexecFromMacro_IsReportedAtTheInvocation_NotInsideTheHeader()
    {
        const string root =
            $"#insert {GshPath};\n" +
            "\n" +
            "REGISTER_SYSTEM( \"animation\", undefined )\n" +
            "\n" +
            "function __init__()\n" +
            "{\n" +
            "}\n";

        ParseResult result = Analyze(root);

        FunctionSymbol autoexec = Assert.Single(
            result.Extraction.Functions, function => function.IsAutoexec);

        // Line 2 (zero-based) is `REGISTER_SYSTEM( "animation", undefined )` in the root file —
        // not line 1 of shared.gsh, where the macro's own `function autoexec __init__sytem__()`
        // text sits.
        Assert.Equal(2, autoexec.NameRange.Start.Line);
        Assert.Equal("", autoexec.SourceFile);

        // FullRange already pointed at the invocation before this fix; the two must now agree.
        Assert.Equal(autoexec.FullRange.Start.Line, autoexec.NameRange.Start.Line);
    }

    [Fact]
    public void PlainInsertedFunction_KeepsTheHeaderAsItsHome()
    {
        // A function written directly in a header (no macro involved) is a different case: it
        // really does live in the .gsh, and go-to-definition should still open that file. Only a
        // macro-produced declaration's position moves to the invocation.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "function shared_fn()\n{\n}\n");

        ParseResult result = ScriptAnalysis.Analyze(
            PreprocessTestHelper.RootPath,
            ScriptLanguage.Gsc,
            SourceText.From($"#insert {GshPath};\n"),
            provider,
            new NameTable());

        FunctionSymbol function = Assert.Single(result.Extraction.Functions);

        Assert.NotEqual("", function.SourceFile);
        Assert.Equal(0, function.NameRange.Start.Line);
    }
}
