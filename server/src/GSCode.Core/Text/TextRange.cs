namespace GSCode.Core.Text;

/// <summary>
/// A half-open span of text: Start is inclusive, End is exclusive. Every range in the
/// codebase follows this convention (the cursor sitting just after a token is outside it).
/// Named TextRange to stay unambiguous next to System.Range.
/// </summary>
public readonly record struct TextRange(Position Start, Position End)
{
    /// <summary>An empty range at the start of a document.</summary>
    public static TextRange Empty { get; } = new(Position.Zero, Position.Zero);

    /// <summary>Creates a range from four line/character coordinates.</summary>
    public static TextRange FromCoordinates(int startLine, int startCharacter, int endLine, int endCharacter)
    {
        return new TextRange(new Position(startLine, startCharacter), new Position(endLine, endCharacter));
    }

    /// <summary>True when the position falls inside the range (start inclusive, end exclusive).</summary>
    public bool Contains(Position position)
    {
        return position >= Start && position < End;
    }

    public override string ToString()
    {
        return $"[{Start}..{End})";
    }
}
