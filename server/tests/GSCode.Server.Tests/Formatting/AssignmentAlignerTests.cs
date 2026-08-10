using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Formatting;

/// <summary>
/// Consecutive alignment for assignments: a run of assignments at the same indentation has its
/// operators lined up one space past the longest left-hand side.
///
/// The aligner runs on already-formatted text, so these format first (align off) and then align,
/// which is exactly the order the server uses.
/// </summary>
public class AssignmentAlignerTests
{
    private static readonly FormatOptions s_aligned =
        FormatOptions.Default with { UseTabs = true, AlignConsecutive = true };

    private static string Format(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        return GscFormatter.Format(result, s_aligned)!;
    }

    [Fact]
    public void OperatorsAlignOnePastTheLongestLeftHandSide()
    {
        string formatted = Format("""
            function f()
            {
            	level.wasp_enabled = true;
            	level.wasp_round_count_blah = 1;
            	level.wasp_round_count += 1;
            }
            """);

        const string expected = "function f()\n"
            + "{\n"
            + "\tlevel.wasp_enabled          = true;\n"
            + "\tlevel.wasp_round_count_blah = 1;\n"
            + "\tlevel.wasp_round_count      += 1;\n"
            + "}\n";

        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void CompoundOperatorsStartAtTheColumn_NotAlignedOnTheEquals()
    {
        // '+' sits at the operator column; the '=' of '+=' is one past. The user's example.
        string formatted = Format("function f()\n{\nlevel.aaaa = 1;\nlevel.b += 2;\n}\n");

        Assert.Contains("\tlevel.aaaa = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tlevel.b    += 2;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankLineEndsARun()
    {
        // Two independent groups, each aligned to its own longest LHS.
        string formatted = Format("""
            function f()
            {
            	a = 1;
            	bbbbbb = 2;

            	cc = 3;
            	d = 4;
            }
            """);

        Assert.Contains("\ta      = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tbbbbbb = 2;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tcc = 3;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\td  = 4;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommentDoesNotBreakARun()
    {
        // The chosen grouping rule: a comment on its own line is transparent, so the assignments
        // above and below it align together.
        string formatted = Format("""
            function f()
            {
            	a = 1;
            	// a note
            	bbbbbb = 2;
            }
            """);

        Assert.Contains("\ta      = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\t// a note\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tbbbbbb = 2;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatementOfADifferentKindEndsARun()
    {
        string formatted = Format("""
            function f()
            {
            	a = 1;
            	bbbbbb = 2;
            	use( a );
            	cc = 3;
            	d = 4;
            }
            """);

        Assert.Contains("\ta      = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tcc = 3;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentIndentationIsADifferentGroup()
    {
        // The nested assignment aligns within its own block, not with the outer one.
        string formatted = Format("""
            function f()
            {
            	aaaa = 1;
            	if ( x )
            	{
            		b = 2;
            	}
            }
            """);

        Assert.Contains("\taaaa = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\t\tb = 2;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoneAssignmentIsNotPadded()
    {
        // A group of one keeps the ordinary single space.
        Assert.Contains("\tlonely = 1;\n", Format("function f()\n{\nlonely = 1;\nuse( lonely );\n}\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEqualsInsideAStringOrCallIsNotMistakenForTheOperator()
    {
        // The operator must be found at top level: `==` in the RHS, and `=` inside the call, are
        // not it. Aligning on the real '=' proves the classifier used tokens, not text.
        string formatted = Format("""
            function f()
            {
            	a = foo( b == c );
            	bbbbbb = "x = y";
            }
            """);

        Assert.Contains("\ta      = foo( b == c );\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tbbbbbb = \"x = y\";\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrailingCommentDoesNotStopAlignment()
    {
        string formatted = Format("function f()\n{\na = 1; // one\nbbbbbb = 2;\n}\n");

        Assert.Contains("\ta      = 1; // one\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tbbbbbb = 2;\n", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void AligningIsIdempotent()
    {
        string once = Format("function f()\n{\na = 1;\nbbbbbb = 2;\ncc = 3;\n}\n");

        ParseResult reparsed = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc", ScriptLanguage.Gsc, SourceText.From(once), NullInsertProvider.Instance, new NameTable());

        Assert.Equal(once, GscFormatter.Format(reparsed, s_aligned));
    }

    [Fact]
    public void OffByDefault_LeavesSingleSpacing()
    {
        // FormatOptions.Default has alignment off, so the same input keeps ordinary spacing.
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\na = 1;\nbbbbbb = 2;\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        string formatted = GscFormatter.Format(result, FormatOptions.Default with { UseTabs = true })!;

        Assert.Contains("\ta = 1;\n", formatted, StringComparison.Ordinal);
        Assert.Contains("\tbbbbbb = 2;\n", formatted, StringComparison.Ordinal);
    }

    // Subscript-interior alignment for array left-hand sides lives in ColumnAlignerTests; this
    // file covers the operator alignment those two aligners compose with.
}
