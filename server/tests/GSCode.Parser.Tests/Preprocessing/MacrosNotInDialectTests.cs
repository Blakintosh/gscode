using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// `#define` and the `#if` chain are Black Ops III's alone — they arrived with the compiler that
/// also brought `#insert`. Everything before it has no preprocessor, and what it has instead is
/// file-scope constants, whose ALL_CAPS naming makes them look convincingly like macros.
///
/// Measured before the rule was written, over the shipped scripts: `#define` appears in exactly one
/// file per IW-line game, always `maps/mp/gametypes/_hud.gsc`, inside a block comment holding C
/// source somebody pasted in. The `#if` family appears in none of the four at all.
///
/// The rule reports and then PROCESSES the directive anyway, which is the behaviour most of these
/// tests are really about — see `GscDiagnosticCode.MacrosNotInDialect`.
/// </summary>
public class MacrosNotInDialectTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    [Theory]
    [InlineData("#define MAX 4\n")]
    [InlineData("#if 1\nfoo();\n#endif\n")]
    public void ADialectWithNoPreprocessorReportsTheDirective(string source)
    {
        PreprocessResult result = PreprocessTestHelper.Run(source, profile: Cod4);

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);

        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
    }

    [Theory]
    [InlineData("#define MAX 4\n")]
    [InlineData("#if 1\nfoo();\n#endif\n")]
    public void BlackOps3ReportsNothing(string source)
    {
        PreprocessResult result = PreprocessTestHelper.Run(source, profile: Bo3);

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);
    }

    [Fact]
    public void TheMacroStillExpands()
    {
        // The point of the whole design. Someone on a custom compiler that does accept macros
        // suppresses 2016 and keeps working IntelliSense; had the directive been skipped instead,
        // suppressing it would leave MAX unresolved with nothing on screen connecting the two.
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define MAX 4\nfunction f()\n{\n    x = MAX;\n}\n", profile: Cod4);

        Assert.Contains("4", PreprocessTestHelper.Texts(result));
        Assert.DoesNotContain("MAX", PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void ItIsReportedOnceForTheWholeFile()
    {
        // The mistake is a property of the file — BO3 syntax against an earlier game, or the wrong
        // game selected — so a file of forty #defines is one problem, not forty.
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define A 1\n#define B 2\n#define C 3\n#if 1\n#endif\n", profile: Cod4);

        Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);
    }

    [Fact]
    public void AnOrphanEndifGetsTheDialectAnswerRatherThanUnexpected()
    {
        // A stray `#endif` under BO3 is genuinely unexpected. Under CoD4 that is true and beside the
        // point: the whole family is absent, so one answer replaces the other rather than joining it.
        PreprocessResult cod4 = PreprocessTestHelper.Run("#endif\n", profile: Cod4);

        Assert.Contains(cod4.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);
        Assert.DoesNotContain(
            cod4.Diagnostics, d => d.Code == GscDiagnosticCode.UnexpectedConditionalDirective);

        PreprocessResult bo3 = PreprocessTestHelper.Run("#endif\n", profile: Bo3);

        Assert.Contains(
            bo3.Diagnostics, d => d.Code == GscDiagnosticCode.UnexpectedConditionalDirective);
    }

    [Fact]
    public void TheMessageNamesTheGame()
    {
        // "Not available here" leaves the reader guessing which half is wrong — the code or the
        // selected game. Naming the game is what makes the second reading available.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n", profile: Cod4);

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);

        Assert.Contains(Cod4.DisplayName, reported.Message, StringComparison.Ordinal);
        Assert.Contains("#define", reported.Message, StringComparison.Ordinal);
    }
}
