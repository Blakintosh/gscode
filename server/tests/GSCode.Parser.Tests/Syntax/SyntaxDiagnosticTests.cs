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

    // --- 3014: a statement with no terminator, reported at the END of that statement ---

    [Fact]
    public void AMissingSemicolonIsReportedOnTheUnterminatedStatement()
    {
        // Shape taken from CoD4's animscripts\traverse\stairs_down.gsc, which really does ship
        // without this semicolon. The report used to land on the NEXT line and name a variable from
        // it — "Expected ';' but found 'horizontalDelta'" — sending the reader to a line that is
        // correct.
        Diagnostic diagnostic = Assert.Single(Analyze(
            "function f()\n{\n\tendPos = endnode.origin\n\thorizontalDelta = ( 1, 2, 0 );\n}\n").AllDiagnostics);

        Assert.Equal(GscDiagnosticCode.MissingSemicolon, diagnostic.Code);

        // Line 2 (0-based) is `endPos = endnode.origin`, NOT line 3 where the next statement starts.
        Assert.Equal(2, diagnostic.Range.Start.Line);

        // One character wide, on the last character of `origin`: a zero-width caret at end of line
        // is the easiest thing in the panel to miss.
        Assert.Equal(2, diagnostic.Range.End.Line);
        Assert.Equal(1, diagnostic.Range.End.Character - diagnostic.Range.Start.Character);

        // The offending token is gone from the message; naming a token from a line further down
        // contradicts where the range now points.
        Assert.DoesNotContain("horizontalDelta", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOffenderOnTheSameLineIsNamedWhereItStands()
    {
        // From CoD4's animscripts\traverse\stairs_up.gsc line 29: `endPos = self endnode.origin`
        // carries a leftover `self` — its sibling stairs_down.gsc writes the statement without it.
        // Nobody puts two statements on one line and forgets the separator, so a token on the SAME
        // line is a stray token, and the fix is to delete one rather than add one. Moving the range
        // to the previous statement would point at code that is fine and hide the token at fault.
        Diagnostic diagnostic = Assert.Single(Analyze(
            "function f()\n{\n\tendPos = self endnode.origin;\n}\n").AllDiagnostics);

        Assert.Equal(GscDiagnosticCode.ExpectedToken, diagnostic.Code);

        // On `endnode` (character 15), and named in the message, because the reader can see it.
        Assert.Equal(2, diagnostic.Range.Start.Line);
        Assert.Equal(15, diagnostic.Range.Start.Character);
        Assert.Contains("endnode", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTokenThatIsNotATerminatorStillReportsAtTheOffender()
    {
        // `function` expects a NAME; the offending token is what should have been the name, so its
        // own range is already the right place to look and nothing about it moves.
        Assert.True(Has("function 123()\n{\n}\n", GscDiagnosticCode.ExpectedToken));
    }

    // --- 4008: '...' somewhere other than last ---

    [Theory]
    [InlineData("function f( ..., a )\n{\n}\n")]
    [InlineData("function f( a, ..., b )\n{\n}\n")]
    [InlineData("function f( ..., ... )\n{\n}\n")]
    public void ThePackMustBeTheLastParameter(string source)
    {
        // A parameter after the pack can never be bound: the pack has already taken everything the
        // caller passed. The parser used to accept this silently, setting HasVarargs and moving on.
        Assert.True(Has(source, GscDiagnosticCode.VarargNotLastParameter));
    }

    [Theory]
    [InlineData("function f( ... )\n{\n}\n")]
    [InlineData("function f( a, b, ... )\n{\n}\n")]
    [InlineData("function f( a, b )\n{\n}\n")]
    public void APackInTheRightPlaceOrNoneAtAll(string source)
    {
        Assert.False(Has(source, GscDiagnosticCode.VarargNotLastParameter));
    }
}
