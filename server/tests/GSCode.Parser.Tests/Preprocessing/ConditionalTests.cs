using GSCode.Core.Diagnostics;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

public class ConditionalTests
{
    [Fact]
    public void If_NonZero_KeepsBranch()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 1\nkept = 1;\n#endif\nafter = 2;");

        Assert.Equal(["kept", "=", "1", ";", "after", "=", "2", ";"], PreprocessTestHelper.Texts(result));
        Assert.Empty(result.DisabledRegions);
    }

    [Fact]
    public void If_Zero_DropsBranchAndRecordsDisabledRegion()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 0\ndropped = 1;\n#endif\nafter = 2;");

        Assert.Equal(["after", "=", "2", ";"], PreprocessTestHelper.Texts(result));
        GSCode.Core.Text.TextRange region = Assert.Single(result.DisabledRegions);
        Assert.Equal(1, region.Start.Line);
    }

    [Fact]
    public void IfElse_TakesElseWhenConditionFails()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 0\na = 1;\n#else\nb = 2;\n#endif");

        Assert.Equal(["b", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void IfElifElse_FirstTrueBranchWins()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 0\na = 1;\n#elif 1\nb = 2;\n#elif 1\nc = 3;\n#else\nd = 4;\n#endif");

        Assert.Equal(["b", "=", "2", ";"], PreprocessTestHelper.Texts(result));
        Assert.Equal(3, result.DisabledRegions.Length);
    }

    [Fact]
    public void If_MacroInCondition_ExpandsBeforeEvaluation()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#define XFILE_VERSION 593\n#if XFILE_VERSION >= 553\nmodern = 1;\n#endif");

        Assert.Equal(["modern", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_UnknownIdentifier_BranchInactive()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if NOT_DEFINED\nx = 1;\n#endif\ny = 2;");

        Assert.Equal(["y", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_ComplexExpression_Evaluates()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if (1 && 0) || (2 > 1 && 3 != 4)\nyes = 1;\n#endif");

        Assert.Equal(["yes", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_Nested_InnerChainRespected()
    {
        string source = """
            #if 1
            outer = 1;
            #if 0
            inner_dropped = 1;
            #else
            inner_kept = 1;
            #endif
            #endif
            """;

        PreprocessResult result = PreprocessTestHelper.Run(source);

        Assert.Equal(["outer", "=", "1", ";", "inner_kept", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_NestedInsideDroppedBranch_FullySkipped()
    {
        string source = """
            #if 0
            #if 1
            never = 1;
            #endif
            #endif
            after = 2;
            """;

        PreprocessResult result = PreprocessTestHelper.Run(source);

        Assert.Equal(["after", "=", "2", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_DirectivesInDroppedBranch_DoNotRegister()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 0\n#define GHOST 1\n#endif\nx = GHOST;");

        Assert.False(result.Macros.TryGet("GHOST", out _));
        Assert.Equal(["x", "=", "GHOST", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void If_Unterminated_Diagnostic()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#if 1\nx = 1;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.UnterminatedConditionalDirective);
        // The open branch still processes.
        Assert.Equal(["x", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void Endif_WithoutIf_Diagnostic()
    {
        PreprocessResult result = PreprocessTestHelper.Run("#endif\nx = 1;");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.UnexpectedConditionalDirective);
        Assert.Equal(["x", "=", "1", ";"], PreprocessTestHelper.Texts(result));
    }
}
