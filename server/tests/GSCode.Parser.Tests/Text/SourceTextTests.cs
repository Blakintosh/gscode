using GSCode.Core.Text;
using Xunit;

namespace GSCode.Parser.Tests.Text;

public class SourceTextTests
{
    [Fact]
    public void From_EmptyText_HasOneLine()
    {
        SourceText text = SourceText.From("");
        Assert.Equal(1, text.LineCount);
        Assert.Equal(new Position(0, 0), text.GetPosition(0));
    }

    [Theory]
    [InlineData("a\nb\nc", 3)]
    [InlineData("a\r\nb\r\nc", 3)]
    [InlineData("a\rb\rc", 3)]
    [InlineData("no breaks", 1)]
    [InlineData("trailing\n", 2)]
    public void From_CountsLines(string source, int expectedLines)
    {
        Assert.Equal(expectedLines, SourceText.From(source).LineCount);
    }

    [Fact]
    public void GetPosition_MapsAcrossCrlfLines()
    {
        SourceText text = SourceText.From("ab\r\ncd");

        Assert.Equal(new Position(0, 0), text.GetPosition(0));
        Assert.Equal(new Position(0, 2), text.GetPosition(2));
        Assert.Equal(new Position(1, 0), text.GetPosition(4));
        Assert.Equal(new Position(1, 2), text.GetPosition(6));
    }

    [Fact]
    public void GetOffset_RoundTripsWithGetPosition()
    {
        SourceText text = SourceText.From("first\nsecond line\nthird");

        for ( int offset = 0; offset <= text.Length; offset++ )
        {
            Position position = text.GetPosition(offset);
            Assert.Equal(offset, text.GetOffset(position));
        }
    }

    [Fact]
    public void GetPosition_SurrogatePair_CountsTwoUnits()
    {
        // "🙂" occupies two UTF-16 code units, so the character after it is at column 3.
        SourceText text = SourceText.From("a🙂b");

        Assert.Equal(new Position(0, 1), text.GetPosition(1));
        Assert.Equal(new Position(0, 3), text.GetPosition(3));
    }

    [Fact]
    public void GetPosition_ClampsOutOfBounds()
    {
        SourceText text = SourceText.From("ab");

        Assert.Equal(new Position(0, 0), text.GetPosition(-5));
        Assert.Equal(new Position(0, 2), text.GetPosition(99));
    }

    [Fact]
    public void Range_Contains_IsHalfOpen()
    {
        TextRange range = TextRange.FromCoordinates(0, 4, 0, 8);

        Assert.True(range.Contains(new Position(0, 4)));
        Assert.True(range.Contains(new Position(0, 7)));
        Assert.False(range.Contains(new Position(0, 8)));
        Assert.False(range.Contains(new Position(0, 3)));
    }

    [Fact]
    public void Position_ComparesLineFirst()
    {
        Assert.True(new Position(1, 0) > new Position(0, 99));
        Assert.True(new Position(2, 3) < new Position(2, 4));
        Assert.True(new Position(5, 5) >= new Position(5, 5));
    }
}
