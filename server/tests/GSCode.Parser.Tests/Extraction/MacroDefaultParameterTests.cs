using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

/// <summary>
/// A macro is a compile-time constant, so it is a legal parameter default however it expands.
/// The spec rule ("plain values only") is about rejecting function references and variables, not
/// about rejecting the preprocessor.
/// </summary>
public class MacroDefaultParameterTests
{
    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable())
            .AllDiagnostics;
    }

    private static bool HasDefaultValueError(ImmutableArray<Diagnostic> diagnostics)
    {
        return diagnostics.Any(diagnostic => diagnostic.Code == GscDiagnosticCode.NonValueDefaultParameter);
    }

    [Theory]
    [InlineData("#define DEFAULT_COUNT 5\n")]
    [InlineData("#define DEFAULT_COUNT (1 << 3)\n")]
    [InlineData("#define DEFAULT_COUNT 2 + 3\n")]
    [InlineData("#define DEFAULT_COUNT ( 0, 0, 0 )\n")]
    public void MacroDefault_IsAllowedHoweverItExpands(string define)
    {
        // The middle two are the reported bug: they expand to something that is not a single
        // literal, so the plain-value check rejected them.
        Assert.False(HasDefaultValueError(Analyze(define + "function f( a = DEFAULT_COUNT )\n{\n}\n")));
    }

    [Fact]
    public void PlainLiteralDefault_IsStillAllowed()
    {
        Assert.False(HasDefaultValueError(Analyze("function f( a = 5, b = \"x\", c = ( 0, 0, 1 ), d = -1 )\n{\n}\n")));
    }

    [Fact]
    public void FunctionCallDefault_IsStillRejected()
    {
        // The rule still has to catch what it exists for.
        Assert.True(HasDefaultValueError(Analyze("function f( a = get_value() )\n{\n}\n")));
    }

    [Fact]
    public void VariableDefault_IsStillRejected()
    {
        Assert.True(HasDefaultValueError(Analyze("function f( a = some_variable )\n{\n}\n")));
    }
}
