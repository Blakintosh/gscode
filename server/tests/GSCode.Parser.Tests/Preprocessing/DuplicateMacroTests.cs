using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// The two duplicate-macro findings, which 1.5 raised and the rewrite did not carry over.
///
/// Both are pure syntax, but they are NOT the same rule wearing two hats. A duplicate parameter has
/// no reading under which the author got what they wanted. A duplicate DEFINITION does: redefining
/// a macro across files is how a script overrides a default its header set, the macro table
/// implements exactly that (last one wins), and reporting it would fire on working code. So the
/// definition rule is scoped to one file's own directives and most of what is below is about the
/// ways that scoping can be got wrong.
/// </summary>
public class DuplicateMacroTests
{
    private const string GshPath = @"scripts\shared\flags.gsh";
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private static int Count(PreprocessResult result, GscDiagnosticCode code)
    {
        int count = 0;
        foreach ( Diagnostic diagnostic in result.Diagnostics )
        {
            if ( diagnostic.Code == code )
            {
                count++;
            }
        }

        return count;
    }

    // --- 2017: two #defines of one name in one file ---

    [Fact]
    public void ANameDefinedTwiceInOneFileIsReported()
    {
        // The control.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n#define MAX 8\nx = MAX;");

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);

        Assert.Contains("MAX", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportingItDoesNotChangeWhichDefinitionWins()
    {
        // The rule describes what happens; it must not alter it. Last definition still wins, so the
        // file the reader is looking at behaves exactly as it did before the diagnostic existed.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n#define MAX 8\nx = MAX;");

        Assert.Equal(["x", "=", "8", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void OnlyTheRedefinitionIsReported()
    {
        // Three definitions are two redefinitions, not three findings.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 1\n#define MAX 2\n#define MAX 3\n");

        Assert.Equal(2, Count(result, GscDiagnosticCode.DuplicateMacroDefinition));
    }

    [Fact]
    public void ANameDefinedOnceIsFine()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n#define MIN 1\n");

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void NamesDifferingOnlyInCaseAreDifferentMacros()
    {
        // Macro names are the case-sensitive exception in this language, and the table is keyed
        // ordinally. A case-insensitive check here would report a pair that genuinely coexists.
        PreprocessResult result = PreprocessTestHelper.Run("#define FOO 1\n#define foo 2\n");

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void AScriptRedefiningItsHeadersMacroIsReported()
    {
        // The case that matters most in practice. Nothing at a call site shows which body it gets;
        // the answer is which definition was seen last, and that depends on where the #insert sits.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#insert {GshPath};\n#define FLAG 2\nx = FLAG;", provider);

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);

        // Names the file being replaced, which is the only part the reader cannot see for themselves.
        Assert.Contains("flags.gsh", reported.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ItIsReportedAtTheDefinitionThatWins()
    {
        // Order decides, so the report goes on the later one — the definition that actually takes
        // effect. Reporting the header's would point at the line the reader has to keep.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#insert {GshPath};\n#define FLAG 2\nx = FLAG;", provider);

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);

        // Line 1 (zero-based) is the `#define FLAG 2` in the root script, not the #insert on line 0.
        Assert.Equal(1, reported.Range.Start.Line);
        Assert.Equal(["x", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void AHeaderRedefiningWhatTheScriptAlreadyDefinedIsReportedToo()
    {
        // The same thing with the order reversed: the #insert comes after, so the header's is the
        // definition that wins, and the header's is the one reported.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 2\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#define FLAG 1\n#insert {GshPath};\nx = FLAG;", provider);

        Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
        Assert.Equal(["x", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void TwoHeadersDefiningTheSameNameIsReported()
    {
        // Neither header wrote the name twice, but a call site in the script still gets whichever
        // #insert came last — which is exactly the thing worth being told about.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n")
            .AddInsert(@"scripts\shared\other.gsh", "#define FLAG 2\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#insert {GshPath};\n#insert scripts\\shared\\other.gsh;\n", provider);

        Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void OneHeaderInsertedTwiceIsFineOnTheReplayPath()
    {
        // With a cache, the second insert replays the recorded definitions rather than walking the
        // header again. Scoping the rule by SourceFile instead of by FRAME would report every macro
        // the header defines, on a file whose only mistake is a redundant #insert.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n#define OTHER 2\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#insert {GshPath};\n#insert {GshPath};\n", provider, new FakeHeaderMacroCache());

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void OneHeaderInsertedTwiceIsFineOnTheRewalkPath()
    {
        // And without a cache, where the header IS walked twice. This is the case the SourceFile
        // scoping got wrong and a per-frame set gets right: the second walk builds a new frame, so
        // it starts with no names of its own.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n#define OTHER 2\n");

        PreprocessResult result = PreprocessTestHelper.Run(
            $"#insert {GshPath};\n#insert {GshPath};\n", provider);

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void AHeaderThatDefinesOneNameTwiceIsStillReported()
    {
        // The control for the scoping: file-scoped does not mean root-file-only.
        FakeInsertProvider provider = new FakeInsertProvider()
            .AddInsert(GshPath, "#define FLAG 1\n#define FLAG 2\n");

        PreprocessResult result = PreprocessTestHelper.Run($"#insert {GshPath};\n", provider);

        Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void ADialectWithNoPreprocessorStillGetsTheDuplicateFinding()
    {
        // CoD4 has no preprocessor, so 2016 fires — and the directive is expanded anyway, because
        // that is what makes suppressing 2016 leave a WORKING file for the case the rule is most
        // likely to be wrong about: a custom compiler that does accept macros.
        //
        // The duplicate rule follows the machinery rather than the dialect for exactly that reason.
        // Gating it on HasMacros would make it Black Ops III's alone, so the same user would
        // `#pragma disable 2016` and lose duplicate-macro checking with it, silently.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n#define MAX 8\n", profile: Cod4);

        Assert.Contains(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.MacrosNotInDialect);
        Assert.Contains(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    [Fact]
    public void SuppressingTheDialectFindingLeavesTheDuplicateOneStanding()
    {
        // The point above, stated as the behaviour a user would see. 2016 is reported once per file;
        // the duplicate is a separate code, so `#pragma disable 2016` does not take it with it.
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX 4\n#define MAX 8\n", profile: Cod4);

        Assert.Single(result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroDefinition);
    }

    // --- 2018: a parameter name written twice on one #define ---

    [Fact]
    public void AParameterNameWrittenTwiceIsReported()
    {
        // The control.
        PreprocessResult result = PreprocessTestHelper.Run("#define PICK(a, a) (a)\n");

        Diagnostic reported = Assert.Single(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroParameter);

        Assert.Contains("'a'", reported.Message, StringComparison.Ordinal);
        Assert.Contains("PICK", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DistinctParameterNamesAreFine()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define ADD(a, b) (a + b)\n");

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroParameter);
    }

    [Fact]
    public void TheMacroStillTakesTheArityItWasWrittenWith()
    {
        // Reported and then added anyway. Dropping the duplicate would leave the macro declaring one
        // parameter while every call site passes two, and the arity check would report all of them.
        PreprocessResult result = PreprocessTestHelper.Run("#define PICK(a, a) (a)\nx = PICK(1, 2);");

        Assert.True(result.Macros.TryGet("PICK", out MacroDefinition definition));
        Assert.Equal(2, definition.Parameters!.Value.Length);
        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.WrongMacroArgumentCount);
    }

    [Fact]
    public void AnObjectLikeMacroHasNoParametersToCollide()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define A (a)\n");

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.DuplicateMacroParameter);
    }
}
