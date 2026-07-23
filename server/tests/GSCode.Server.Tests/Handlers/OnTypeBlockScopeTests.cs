using GSCode.Server.Formatting;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// On-type formatting scopes its edits to the alignment GROUP around the cursor — the run of lines
/// that actually re-flow together — so a keystroke tidies the run you are editing and nothing past
/// it. A run of assignments is one group; a statement of a different kind ends it.
/// </summary>
public class OnTypeBlockScopeTests
{
    [Fact]
    public void AnAssignmentRunStopsAtAStatementOfADifferentKind()
    {
        // The reported case: editing a subscript assignment must not reach the bare calls around
        // it, even with no blank line between them.
        const string doc =
            "function f()\n" +          // 0
            "{\n" +                     // 1
            "\ta();\n" +                // 2  call
            "\tfoo[ \"x\" ] = 1;\n" +   // 3  assignment
            "\tbash[ \"yy\" ] = 2;\n" + // 4  assignment
            "\tb();\n" +                // 5  call
            "}\n";                      // 6

        (int top, int bottom) = FormatScope.GroupAround(doc, 4);

        Assert.Equal(3, top);
        Assert.Equal(4, bottom);
    }

    [Fact]
    public void AllConsecutiveAssignmentsAreOneGroup()
    {
        // The operator aligner spans any consecutive assignments, so the scope must too.
        const string doc =
            "function f()\n" +
            "{\n" +
            "\tplain = 1;\n" +      // 2
            "\tfoo[ \"x\" ] = 2;\n" + // 3
            "\tother += 3;\n" +     // 4
            "}\n";

        (int top, int bottom) = FormatScope.GroupAround(doc, 3);

        Assert.Equal(2, top);
        Assert.Equal(4, bottom);
    }

    [Fact]
    public void ACommentInTheRunIsTransparent()
    {
        const string doc =
            "\ta = 1;\n" +      // 0
            "\t// a note\n" +   // 1
            "\tbb = 2;\n";      // 2

        (int top, int bottom) = FormatScope.GroupAround(doc, 0);

        Assert.Equal(0, top);
        Assert.Equal(2, bottom);
    }

    [Fact]
    public void ABlankLineEndsTheRun()
    {
        const string doc =
            "\ta = 1;\n" +   // 0
            "\tbb = 2;\n" +  // 1
            "\n" +           // 2
            "\tcc = 3;\n";   // 3

        (int top, int bottom) = FormatScope.GroupAround(doc, 1);

        Assert.Equal(0, top);
        Assert.Equal(1, bottom);
    }

    [Fact]
    public void CallsGroupOnlyWithTheSameCallee()
    {
        const string doc =
            "\tregister( \"a\", 1 );\n" +  // 0
            "\tregister( \"bb\", 2 );\n" + // 1
            "\tspawn( \"c\", 3 );\n";      // 2

        (int top, int bottom) = FormatScope.GroupAround(doc, 0);

        Assert.Equal(0, top);
        Assert.Equal(1, bottom);
    }

    [Fact]
    public void ADifferentIndentEndsTheRun()
    {
        const string doc =
            "\ta = 1;\n" +       // 0
            "\t\tnested = 2;\n" + // 1  deeper
            "\tb = 3;\n";        // 2

        (int top, int bottom) = FormatScope.GroupAround(doc, 1);

        Assert.Equal(1, top);
        Assert.Equal(1, bottom);
    }

    [Fact]
    public void ANonAlignableLineIsScopedToItself()
    {
        const string doc =
            "\ta = 1;\n" +      // 0
            "\treturn x;\n" +   // 1  not an assignment or call
            "\tb = 2;\n";       // 2

        (int top, int bottom) = FormatScope.GroupAround(doc, 1);

        Assert.Equal(1, top);
        Assert.Equal(1, bottom);
    }

    [Fact]
    public void AnOutOfRangeLineIsClamped()
    {
        (int top, int bottom) = FormatScope.GroupAround("a = 1;\n", 99);

        Assert.True(top >= 0 && bottom >= top);
    }
}
