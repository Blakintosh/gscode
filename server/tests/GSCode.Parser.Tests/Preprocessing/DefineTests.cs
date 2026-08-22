using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

public class DefineTests
{
    [Fact]
    public void Define_ObjectLike_RegistersAndExpands()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define PI 3.14\nx = PI;");

        Assert.True(result.Macros.TryGet("PI", out MacroDefinition definition));
        Assert.False(definition.IsFunctionLike);
        Assert.Equal(["x", "=", "3.14", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_NamesAreCaseSensitive()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define FOO 1\nx = foo;");

        // foo (lowercase) is NOT the macro FOO — macro names are the case-sensitive exception.
        Assert.Equal(["x", "=", "foo", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_KeywordName_Works()
    {
        // TRUE lexes as the True keyword; macros may still be named TRUE.
        PreprocessResult result = PreprocessTestHelper.Run("#define TRUE 1\nx = TRUE;");

        Assert.Equal(["x", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_FunctionLike_SubstitutesArguments()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define SQ(x) (x * x)\ny = SQ(4);");

        Assert.Equal(["y", "=", "(", "4", "*", "4", ")", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_ParenNotAdjacent_IsObjectLike()
    {
        // "#define A (x)" — the space makes it object-like with body "(x)".
        PreprocessResult result = PreprocessTestHelper.Run("#define A (x)\nv = A;");

        Assert.True(result.Macros.TryGet("A", out MacroDefinition definition));
        Assert.False(definition.IsFunctionLike);
        Assert.Equal(["v", "=", "(", "x", ")", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_MultilineBody_ContinuesWithBackslash()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define BIG a + \\\n b\nx = BIG;");

        Assert.Equal(["x", "=", "a", "+", "b", ";"], PreprocessTestHelper.Texts(result));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Define_StrayBackslash_DiagnosticAndExcluded()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define BAD a \\ b\nx = BAD;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.InvalidLineContinuation);
        Assert.Equal(["x", "=", "a", "b", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_NestedMacros_ResolveRecursively()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define A 1\n#define B A + A\nx = B;");

        Assert.Equal(["x", "=", "1", "+", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_SelfRecursion_DoesNotLoop()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define LOOP LOOP + 1\nx = LOOP;");

        // The inner LOOP stays unexpanded (C-style self-recursion guard).
        Assert.Equal(["x", "=", "LOOP", "+", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_FunctionLikeUsedWithoutParens_DiagnosticNoExpansion()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define F(x) x\ny = F;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.MissingMacroArguments);
        Assert.Equal(["y", "=", "F", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_BlankArgument_ExpandsToNothing()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define PAIR(a, b) a b\nx = PAIR(, 2);");

        Assert.Equal(["x", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_MissingName_Diagnostic()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define\nx = 1;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.ExpectedMacroName);
        Assert.Equal(["x", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_TrailingComment_BecomesDocumentation()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define MAX_HEALTH 100 // full health\n");

        Assert.True(result.Macros.TryGet("MAX_HEALTH", out MacroDefinition definition));
        Assert.Equal("// full health", definition.Documentation);
    }

    [Fact]
    public void Define_Redefinition_LastWins()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define V 1\n#define V 2\nx = V;");

        Assert.Equal(["x", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Define_InvocationsAreRecorded()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define PI 3.14\nx = PI;\ny = PI;");

        Assert.Equal(2, result.MacroInvocations.Length);
        Assert.All(result.MacroInvocations, invocation => Assert.Equal("PI", invocation.Name));
        Assert.All(result.MacroInvocations, invocation => Assert.Null(invocation.SourceFile));
    }

    [Fact]
    public void Define_ExpandedTokens_CarryDefinitionSiteAndRootSite()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define PI 3.14\nx = PI;");

        PToken expanded = Assert.Single(result.Tokens, token => token.Text == "3.14");
        Assert.NotNull(expanded.Provenance.DefinitionSite);
        Assert.NotNull(expanded.Provenance.RootSite);

        // The root site is the invocation (line 1), not the definition (line 0).
        Assert.Equal(1, expanded.Provenance.RootSite!.Value.Start.Line);
        Assert.Equal(0, expanded.Provenance.DefinitionSite!.Value.Start.Line);
    }

    // --- 2015: a function-like macro invoked with the wrong number of arguments ---

    private static bool HasArgumentCountError(string source)
    {
        return PreprocessTestHelper.Run(source).Diagnostics
            .Any(diagnostic => diagnostic.Code == GscDiagnosticCode.WrongMacroArgumentCount);
    }

    [Theory]
    [InlineData("#define ADD( a, b ) ( a + b )\nx = ADD( 1 );\n")]
    [InlineData("#define ADD( a, b ) ( a + b )\nx = ADD( 1, 2, 3 );\n")]
    [InlineData("#define ADD( a, b ) ( a + b )\nx = ADD();\n")]
    [InlineData("#define ONE( a ) ( a )\nx = ONE();\n")]
    public void AMacroInvokedWithTheWrongCount(string source)
    {
        // EXACT, unlike a call to a script function, where passing fewer arguments than declared is
        // legal and the rest are undefined. A macro is textual substitution: a parameter with no
        // argument leaves its own name sitting in the expansion. So both directions are wrong here.
        Assert.True(HasArgumentCountError(source));
    }

    [Theory]
    [InlineData("#define ADD( a, b ) ( a + b )\nx = ADD( 1, 2 );\n")]
    [InlineData("#define NONE() ( 0 )\nx = NONE();\n")]
    [InlineData("#define ONE( a ) ( a )\nx = ONE( 5 );\n")]
    public void AMacroInvokedCorrectly(string source)
    {
        Assert.False(HasArgumentCountError(source));
    }

    [Fact]
    public void NoArgumentsIsZeroRatherThanOneEmptyOne()
    {
        // The collector always holds at least one group, so `NONE()` arrives as a single EMPTY group.
        // Counting groups rather than recognising that case would read it as one argument and report
        // every correct zero-argument invocation.
        Assert.False(HasArgumentCountError("#define NONE() ( 0 )\nx = NONE();\n"));
    }

    [Fact]
    public void AnArgumentMayItselfContainCommas()
    {
        // Commas inside nested parentheses belong to the inner expression, so this is ONE argument.
        Assert.False(HasArgumentCountError("#define ONE( a ) ( a )\nx = ONE( f( 1, 2 ) );\n"));
    }

    [Fact]
    public void AnObjectLikeMacroIsNotJudged()
    {
        // `PI` declares no parameter list, so a following `(` is the author's own parenthesis and
        // there is no argument count to be wrong about.
        Assert.False(HasArgumentCountError("#define PI 3.14\nx = PI;\ny = ( PI );\n"));
    }
}
