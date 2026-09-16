using GSCode.Core.Diagnostics;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// A macro name written as one of a NESTED call's arguments inside another macro's body —
/// <c>#define WRAP() INNER(VALUE)</c>, where VALUE is itself a <c>#define</c>. Only a parameter
/// reference of the OUTER macro spliced through here; a genuine macro name stayed as the literal
/// identifier, the nested call it fed recorded no <see cref="MacroInvocation"/>, and a mismatched
/// argument count went unchecked — all three because <c>TryCollectBodyArguments</c> asked a
/// narrower question than <c>ExpandBody</c>'s own loop asks for exactly the same shape of token.
/// </summary>
public class NestedMacroBodyExpansionTests
{
    [Fact]
    public void AMacroNameUsedAsAnArgumentInAMacrosBodyExpands()
    {
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define VALUE 5\n#define INNER(y) (y)\n#define WRAP() INNER(VALUE)\nz = WRAP();");

        Assert.Equal(["z", "=", "(", "5", ")", ";"], PreprocessTestHelper.Texts(result));
    }

    [Fact]
    public void TheNestedCallRecordsAnInvocationForTheMacroItPassedAsAnArgument()
    {
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define VALUE 5\n#define INNER(y) (y)\n#define WRAP() INNER(VALUE)\nz = WRAP();");

        Assert.Contains(result.MacroInvocations, invocation => invocation.Name == "VALUE");
    }

    [Fact]
    public void TheNestedCallItselfRecordsAnInvocationToo()
    {
        // Not just the argument-expansion fix: a nested call whose arguments needed no expansion at
        // all recorded nothing either, before this existed.
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define INNER(y) (y)\n#define WRAP() INNER(1)\nz = WRAP();");

        Assert.Contains(result.MacroInvocations, invocation => invocation.Name == "INNER");
    }

    [Fact]
    public void AMismatchedArgumentCountOnANestedCallIsReported()
    {
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define INNER(y) (y)\n#define WRAP() INNER(1, 2)\nz = WRAP();");

        Assert.Contains(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.WrongMacroArgumentCount);
    }

    [Fact]
    public void AMatchingArgumentCountOnANestedCallIsFine()
    {
        PreprocessResult result = PreprocessTestHelper.Run(
            "#define INNER(y) (y)\n#define WRAP() INNER(1)\nz = WRAP();");

        Assert.DoesNotContain(
            result.Diagnostics, d => d.Code == GscDiagnosticCode.WrongMacroArgumentCount);
    }
}
