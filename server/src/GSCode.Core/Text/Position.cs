namespace GSCode.Core.Text;

/// <summary>
/// A zero-based position in a document. Character counts UTF-16 code units,
/// matching the LSP default encoding, so mapping to protocol positions is identity.
/// </summary>
public readonly record struct Position(int Line, int Character) : IComparable<Position>
{
    /// <summary>The start of a document (line 0, character 0).</summary>
    public static Position Zero { get; } = new(0, 0);

    /// <summary>Orders positions by line, then by character within the line.</summary>
    public int CompareTo(Position other)
    {
        if ( Line != other.Line )
        {
            return Line.CompareTo(other.Line);
        }

        return Character.CompareTo(other.Character);
    }

    public static bool operator <(Position left, Position right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(Position left, Position right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(Position left, Position right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(Position left, Position right)
    {
        return left.CompareTo(right) >= 0;
    }

    public override string ToString()
    {
        return $"{Line}:{Character}";
    }
}
