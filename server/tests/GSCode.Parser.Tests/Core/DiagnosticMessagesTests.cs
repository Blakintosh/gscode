using GSCode.Core.Diagnostics;
using Xunit;

namespace GSCode.Parser.Tests.Core;

public class DiagnosticMessagesTests
{
    /// <summary>
    /// Every code has a template. <see cref="DiagnosticMessages.Format"/> indexes the frozen
    /// dictionary directly, so a missing entry would throw here rather than at some later,
    /// harder-to-diagnose call site.
    /// </summary>
    [Fact]
    public void EveryDiagnosticCodeHasATemplate()
    {
        foreach ( GscDiagnosticCode code in Enum.GetValues<GscDiagnosticCode>() )
        {
            string message = DiagnosticMessages.Format(code);
            Assert.NotNull(message);
        }
    }

    /// <summary>
    /// No formatted message leaks a literal escaped brace. <see cref="DiagnosticMessages.Format"/>
    /// only unescapes '{{'/'}}' when it has arguments to pass to string.Format — a zero-argument
    /// code returns the raw template, so a message written with '{{'/'}}' (to survive
    /// string.Format when the code DOES take arguments) must never be called with none.
    /// </summary>
    [Fact]
    public void NoFormattedMessageContainsAnEscapedBrace()
    {
        foreach ( GscDiagnosticCode code in Enum.GetValues<GscDiagnosticCode>() )
        {
            string message = DiagnosticMessages.Format(code);
            Assert.DoesNotContain("{{", message);
            Assert.DoesNotContain("}}", message);
        }
    }

    [Fact]
    public void UnterminatedBlockMessageRendersASingleClosingBrace()
    {
        string message = DiagnosticMessages.Format(GscDiagnosticCode.UnterminatedBlock);

        Assert.Equal("Block is missing its closing '}'.", message);
    }
}
