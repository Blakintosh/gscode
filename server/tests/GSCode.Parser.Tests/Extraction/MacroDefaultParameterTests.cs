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
/// Parameter defaults are unrestricted: the value is evaluated in the function BODY when the
/// argument arrives undefined, so anything the body could contain is legal there.
///
/// These began as tests that a MACRO default survived a "plain values only" rule. That rule is
/// gone — it reported 21 errors across 8 shipped scripts, on empty arrays, function pointers and
/// member reads that core files use — so what they now pin is that nothing is reported at all.
/// Kept rather than deleted because the macro cases were a real reported bug, and a future
/// narrowing of the rule must not resurrect it.
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

    [Theory]
    [InlineData("a = get_value()")]
    [InlineData("a = some_variable")]
    [InlineData("reqs = []")]
    [InlineData("give_fn = &default_give")]
    public void NothingIsRejectedAnyMore(string parameters)
    {
        // The last two are why the rule went: `function register( ..., reqs = [] )` in
        // system_shared is called from everywhere, and `&default_give` is an ordinary callback
        // default. Both were reported as errors.
        Assert.False(HasDefaultValueError(Analyze($"function f( {parameters} )\n{{\n}}\n")));
    }
}
