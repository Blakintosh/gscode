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
/// Code after a statement that always leaves the block. GSC has no labels or gotos, so nothing can
/// jump back in and the question is syntactic rather than a dataflow one — which is what makes the
/// answer trustworthy enough to report at all.
///
/// A Hint with the Unnecessary tag: dead code is usually a leftover, and the useful thing is to
/// SEE it greyed out. An error on something that does no harm would be nagging.
/// </summary>
public class UnreachableCodeLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\n" + body + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return UnreachableCodeLint.Analyze(result);
    }

    [Theory]
    [InlineData("\treturn;\n\tx = 1;")]
    [InlineData("\treturn 5;\n\tx = 1;")]
    public void AfterAReturn(string body)
    {
        Diagnostic diagnostic = Assert.Single(Lint(body));

        Assert.Equal(GscDiagnosticCode.UnreachableCode, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
        Assert.Contains("return", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterABreakOrContinueInsideALoop()
    {
        Assert.Single(Lint("\twhile ( 1 )\n\t{\n\t\tbreak;\n\t\tx = 1;\n\t}"));
        Assert.Single(Lint("\twhile ( 1 )\n\t{\n\t\tcontinue;\n\t\tx = 1;\n\t}"));
    }

    [Fact]
    public void TheWholeRunIsOneDiagnostic()
    {
        // One terminator is one cause. Five greyed statements would be five ways of saying it.
        ImmutableArray<Diagnostic> diagnostics = Lint("\treturn;\n\ta = 1;\n\tb = 2;\n\tc = 3;");

        Diagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(3, diagnostic.Range.Start.Line);
        Assert.Equal(5, diagnostic.Range.End.Line);
    }

    [Fact]
    public void ATerminatorAtTheEndOfItsBlockIsFine()
    {
        // The overwhelmingly common case, and the one a careless implementation flags.
        Assert.Empty(Lint("\tx = 1;\n\treturn;"));
    }

    [Fact]
    public void AReturnInsideABranchDoesNotKillWhatFollOwsTheBranch()
    {
        // The `if` may not run at all, so the statement after it is perfectly reachable. Only a
        // SIBLING terminator earlier in the SAME block counts.
        Assert.Empty(Lint("\tif ( x )\n\t{\n\t\treturn;\n\t}\n\n\tuse( 1 );"));
    }

    [Fact]
    public void ABreakEndsOnlyItsOwnBlock()
    {
        Assert.Empty(Lint("\twhile ( 1 )\n\t{\n\t\tif ( x )\n\t\t{\n\t\t\tbreak;\n\t\t}\n\n\t\tuse( 1 );\n\t}"));
    }

    [Fact]
    public void ACaseThatBreaksDoesNotKillTheNextCase()
    {
        // Each case group is its own run of statements; a break ends the switch, not the labels
        // after it. Treating the whole switch as one block would report every later case as dead.
        Assert.Empty(Lint(
            "\tswitch ( x )\n\t{\n\t\tcase 1:\n\t\t\tbreak;\n\t\tcase 2:\n\t\t\tuse( 1 );\n\t\t\tbreak;\n\t}"));
    }

    [Fact]
    public void NestedFunctionsAndClassesAreWalked()
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("class C\n{\n\tfunction m()\n\t{\n\t\treturn;\n\t\tx = 1;\n\t}\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        Assert.Single(UnreachableCodeLint.Analyze(result));
    }

    [Fact]
    public void AFunctionInsideATopLevelDevBlockIsWalked()
    {
        // A declaration-level `/# … #/` wraps whole functions, and one was previously not descended
        // into at all — the walker enumerated each container by hand and this one was missing, so
        // dead code inside it went unreported. Nothing in the shipped corpora happens to hit the
        // shape, which is exactly why it needs a test rather than a corpus run to hold it.
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("/#\nfunction dbg()\n{\n\treturn;\n\tx = 1;\n}\n#/\n"),
            NullInsertProvider.Instance,
            new NameTable());

        Diagnostic diagnostic = Assert.Single(UnreachableCodeLint.Analyze(result));
        Assert.Equal(GscDiagnosticCode.UnreachableCode, diagnostic.Code);
    }
}
