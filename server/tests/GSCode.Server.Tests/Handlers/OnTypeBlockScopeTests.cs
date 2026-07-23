using GSCode.Server.Handlers;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// On-type formatting scopes its edits to the contiguous run of non-blank lines around the cursor,
/// so a keystroke tidies the block being edited rather than the whole file. The run is bounded by
/// blank lines, which is exactly where an alignment group ends — so the group is always contained
/// whole and never half-aligned.
/// </summary>
public class OnTypeBlockScopeTests
{
    private const string Doc =
        "function f()\n" +    // 0
        "{\n" +               // 1
        "\tfoo = 1;\n" +      // 2
        "\tothershit = 2;\n" + // 3
        "\n" +                // 4  (blank)
        "\tlater = 3;\n" +    // 5
        "}\n";                // 6

    [Fact]
    public void TheBlockIsBoundedByBlankLines()
    {
        // Typing ';' on line 3 scopes to the run 0..3 (the function header, brace and both
        // assignments) and stops at the blank line 4.
        (int top, int bottom) = DocumentOnTypeFormattingHandler.BlockAround(Doc, 3);

        Assert.Equal(0, top);
        Assert.Equal(3, bottom);
    }

    [Fact]
    public void ASeparateBlockBelowTheBlankIsItsOwn()
    {
        (int top, int bottom) = DocumentOnTypeFormattingHandler.BlockAround(Doc, 5);

        Assert.Equal(5, top);
        Assert.Equal(6, bottom);
    }

    [Fact]
    public void ASingleLineBetweenBlanksIsJustItself()
    {
        (int top, int bottom) = DocumentOnTypeFormattingHandler.BlockAround("a\n\nb\n\nc\n", 2);

        Assert.Equal(2, top);
        Assert.Equal(2, bottom);
    }

    [Fact]
    public void AnOutOfRangeLineIsClamped()
    {
        // Defensive: a position past the end must not throw.
        (int top, int bottom) = DocumentOnTypeFormattingHandler.BlockAround("a\nb\n", 99);

        Assert.True(top >= 0 && bottom >= top);
    }
}
