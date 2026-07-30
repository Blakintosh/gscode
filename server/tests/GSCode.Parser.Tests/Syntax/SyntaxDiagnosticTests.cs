using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// The diagnostics decidable from syntax alone: an assignment where a comparison was meant, a
/// parameter declared twice, and an <c>#insert</c> naming something that is not a header.
///
/// None needs the workspace, the index or type inference, which is what makes them safe to report
/// as Errors — there is no evidence that could arrive later and change the answer.
/// </summary>
public class SyntaxDiagnosticTests
{
    private static ParseResult Analyze(string source)
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
    }

    private static bool Has(string source, GscDiagnosticCode code)
    {
        return Analyze(source).AllDiagnostics.Any(diagnostic => diagnostic.Code == code);
    }

    // --- 3013: an assignment used as a condition ---

    [Theory]
    [InlineData("if ( x = 5 )\n\t{\n\t}")]
    [InlineData("while ( x = 5 )\n\t{\n\t}")]
    [InlineData("for ( i = 0; x = 5; i++ )\n\t{\n\t}")]
    public void AnAssignmentWhereAComparisonWasMeant(string body)
    {
        Assert.True(Has(
            "function f()\n{\n\t" + body + "\n}\n", GscDiagnosticCode.AssignmentUsedAsCondition));
    }

    [Fact]
    public void ParenthesesSayTheAssignmentIsDeliberate()
    {
        // The escape hatch every C-family compiler uses for this warning, and the reason the check
        // looks only at a BARE assignment.
        Assert.False(Has(
            "function f()\n{\n\tif ( ( x = next() ) )\n\t{\n\t}\n}\n",
            GscDiagnosticCode.AssignmentUsedAsCondition));
    }

    [Theory]
    [InlineData("if ( x == 5 )\n\t{\n\t}")]
    [InlineData("if ( isdefined( x ) )\n\t{\n\t}")]
    [InlineData("if ( a && b )\n\t{\n\t}")]
    public void AnOrdinaryConditionIsFine(string body)
    {
        Assert.False(Has(
            "function f()\n{\n\t" + body + "\n}\n", GscDiagnosticCode.AssignmentUsedAsCondition));
    }

    [Fact]
    public void ACompoundAssignmentIsNotAComparisonTypo()
    {
        // `+=` is not a plausible slip for `==`, so reporting it would be noise.
        Assert.False(Has(
            "function f()\n{\n\twhile ( x += 1 )\n\t{\n\t}\n}\n",
            GscDiagnosticCode.AssignmentUsedAsCondition));
    }

    // --- 4007: a parameter declared twice ---

    [Fact]
    public void AParameterDeclaredTwice()
    {
        // Unreachable rather than ambiguous: every call binds the later one, so the earlier
        // parameter can never be read and one argument is silently lost.
        Assert.True(Has("function f( a, a )\n{\n}\n", GscDiagnosticCode.DuplicateParameter));
    }

    [Fact]
    public void DuplicateParameterIsCaseInsensitive()
    {
        // GSC resolves names case-insensitively, so these are one parameter.
        Assert.True(Has("function f( count, COUNT )\n{\n}\n", GscDiagnosticCode.DuplicateParameter));
    }

    [Fact]
    public void DistinctParametersAreFine()
    {
        Assert.False(Has("function f( a, b, c )\n{\n}\n", GscDiagnosticCode.DuplicateParameter));
    }

    // --- 2014: #insert naming something that is not a header ---

    [Fact]
    public void InsertingAScriptRatherThanAHeader()
    {
        // Naming a script resolves to a real file and pastes its declarations into the middle of
        // this one, so the errors surface far from the directive and look nothing like the cause.
        Assert.True(Has(
            "#insert scripts\\shared\\util_shared.gsc;\n", GscDiagnosticCode.InsertNotAHeader));
    }

    [Fact]
    public void InsertingAHeaderIsFine()
    {
        Assert.False(Has(
            "#insert scripts\\shared\\shared.gsh;\n", GscDiagnosticCode.InsertNotAHeader));
    }
}
