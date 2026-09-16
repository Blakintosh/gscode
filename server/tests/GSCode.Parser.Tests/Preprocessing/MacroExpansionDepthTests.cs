using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// Macro argument collection recurses without a depth cap: <c>TryExpandAt</c> calls
/// <c>TryCollectArguments</c>, which — scanning a function-like macro's arguments for nested macro
/// uses — calls <c>TryExpandAt</c> again. Unlike <see cref="ConditionalEvaluator"/>'s paren nesting
/// (capped at 64) and the parser's own tree depth (capped at 512), nothing bounds how deeply a CALL
/// SITE may nest a function-like macro's own name — <c>F(F(F(…</c> — so this is purely a function of
/// how much text the author (or a pathological input) writes, unrelated to the number of distinct
/// macros the file defines.
/// </summary>
public class MacroExpansionDepthTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private static PreprocessResult ProcessWithinBudget(string source)
    {
        Task<PreprocessResult> preprocess = Task.Run(() =>
        {
            SourceText text = SourceText.From(source);
            LexResult lexed = Lexer.Lex(text);
            return Preprocessor.Process(
                PreprocessTestHelper.RootPath, lexed.Tokens, text, NullInsertProvider.Instance, new GSCode.Core.NameTable());
        });

        Assert.True(
            preprocess.Wait(Budget),
            $"preprocessing did not finish within {Budget.TotalSeconds}s on this input");

        return preprocess.Result;
    }

    private static string NestedCall(int depth)
    {
        return "#define F(x) x\ny = " + string.Concat(Enumerable.Repeat("F(", depth)) + "1"
            + string.Concat(Enumerable.Repeat(")", depth)) + ";";
    }

    [Fact]
    public void APathologicallyNestedMacroCallSurvives()
    {
        // Comfortably past the measured overflow point for this shape (matching ParserDepthTests'
        // own convention) — a regression here is a dead test process rather than a red test, which
        // is itself the signal.
        PreprocessResult result = ProcessWithinBudget(NestedCall(20_000));

        // Bounded: the point of a depth cap is that it stops, not merely that it eventually returns.
        Assert.NotEmpty(result.Tokens);
    }

    [Fact]
    public void OrdinaryNestedMacroCallsStillExpandCorrectly()
    {
        // Comfortably under any sane cap, so the guard must not touch code that actually works.
        PreprocessResult result = ProcessWithinBudget(NestedCall(20));

        Assert.Equal("1", PreprocessTestHelper.Texts(result)[^2]);
    }
}
