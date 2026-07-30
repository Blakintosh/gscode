using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// A `case` label has to be a compile-time constant.
///
/// `case undefined:` is the one people write: it parses fine and looks like a value, but nothing
/// equals undefined in a switch, so the branch is silently unreachable rather than obviously
/// wrong. What counts as constant comes from the stock scripts — all 1,918 case labels there are
/// strings, integers or macros — and the lint finds none of them, which is the check that matters.
/// </summary>
public class CaseLabelLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string cases, string preamble = "")
    {
        string source = preamble
            + "function f( v )\n{\n    switch ( v )\n    {\n" + cases + "\n    }\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return CaseLabelLint.Analyze(result);
    }

    [Fact]
    public void CaseUndefinedIsReported()
    {
        Diagnostic bad = Assert.Single(Lint("        case undefined:\n            break;"));

        Assert.Equal(GscDiagnosticCode.CaseUndefined, bad.Code);
        // The message explains why the branch is dead rather than just naming the rule.
        Assert.Contains("never matches", bad.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("        case \"ready\":\n            break;")]
    [InlineData("        case 3:\n            break;")]
    [InlineData("        case -1:\n            break;")]
    [InlineData("        case ( 2 ):\n            break;")]
    [InlineData("        case true:\n            break;")]
    [InlineData("        default:\n            break;")]
    [InlineData("        case \"a\":\n        case \"b\":\n            break;")]
    public void ConstantLabelsAreFine(string cases)
    {
        Assert.Empty(Lint(cases));
    }

    [Fact]
    public void AMacroLabelIsFine()
    {
        // The preprocessor has already made it constant; it is judged on what it expanded to.
        Assert.Empty(Lint(
            "        case MAX_PLAYERS:\n            break;",
            preamble: "#define MAX_PLAYERS 18\n"));
    }

    [Theory]
    [InlineData("        case other:\n            break;")]
    [InlineData("        case get_value():\n            break;")]
    [InlineData("        case self.field:\n            break;")]
    public void NonConstantLabelsAreReported(string cases)
    {
        Assert.Equal(GscDiagnosticCode.NonConstantCaseLabel, Assert.Single(Lint(cases)).Code);
    }

    [Fact]
    public void UndefinedGetsItsOwnMessageRatherThanTheGenericOne()
    {
        // "not a constant" would not tell anyone what to do; the fix is a different construct.
        Assert.NotEqual(
            Assert.Single(Lint("        case undefined:\n            break;")).Code,
            Assert.Single(Lint("        case other:\n            break;")).Code);
    }

    [Fact]
    public void ASwitchNestedInsideAnotherIsStillChecked()
    {
        // The walk has to reach statements inside a case body, not just the top level.
        string source =
            "function f( v, w )\n{\n    switch ( v )\n    {\n        case 1:\n"
            + "            switch ( w )\n            {\n                case undefined:\n                    break;\n            }\n"
            + "            break;\n    }\n}\n";

        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        Assert.Equal(GscDiagnosticCode.CaseUndefined, Assert.Single(CaseLabelLint.Analyze(result)).Code);
    }

    // --- 5017: a label the switch already has ---

    [Fact]
    public void ALabelTheSwitchAlreadyHas()
    {
        // Only the first can ever match, so the second branch is unreachable — the same class of
        // finding as 5015, but invisible in the code's shape: nothing about the second `case` looks
        // wrong on its own.
        Assert.Contains(
            Lint("        case 1:\n            break;\n        case 1:\n            break;"),
            diagnostic => diagnostic.Code == GscDiagnosticCode.DuplicateCaseLabel);
    }

    [Fact]
    public void DuplicatesAreFoundAcrossCaseGroups()
    {
        // Grouping is a formatting choice rather than a scope, so the check spans the whole switch.
        Assert.Contains(
            Lint("        case \"a\":\n        case \"b\":\n            break;\n        case \"a\":\n            break;"),
            diagnostic => diagnostic.Code == GscDiagnosticCode.DuplicateCaseLabel);
    }

    [Fact]
    public void StringLabelsAreComparedCaseSensitively()
    {
        // A string label is matched by VALUE, so "A" and "a" are different events — unlike a
        // function name, which GSC resolves case-insensitively.
        Assert.DoesNotContain(
            Lint("        case \"A\":\n            break;\n        case \"a\":\n            break;"),
            diagnostic => diagnostic.Code == GscDiagnosticCode.DuplicateCaseLabel);
    }

    [Fact]
    public void DistinctLabelsAndDefaultAreFine()
    {
        Assert.DoesNotContain(
            Lint("        case 1:\n            break;\n        case 2:\n            break;\n        default:\n            break;"),
            diagnostic => diagnostic.Code == GscDiagnosticCode.DuplicateCaseLabel);
    }
}
