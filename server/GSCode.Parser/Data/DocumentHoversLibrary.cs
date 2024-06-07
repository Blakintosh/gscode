using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace GSCode.Parser.Data;

public sealed class DocumentHoversLibrary
{
    private SortedList<int, IHoverable>?[] BackingHovers { get; }

    public DocumentHoversLibrary(int lineCount)
    {
        BackingHovers = new SortedList<int, IHoverable>?[lineCount];
    }
    
    /// <summary>
    /// Gets the hover that corresponds to the given position if it exists.
    /// </summary>
    /// <param name="location">The target location</param>
    /// <returns>An IHoverable instance corresponding to the position if it exists, or null otherwise</returns>
    public IHoverable? Get(Position location)
    {
        SortedList<int, IHoverable>? lineList = BackingHovers[location.Line];

        if(lineList is null)
        {
            return null;
        }

        foreach(KeyValuePair<int, IHoverable> hoverableKvp in lineList)
        {
            IHoverable hoverable = hoverableKvp.Value;
            if(hoverable.Range.Start.Character <= location.Character &&
                hoverable.Range.End.Character > location.Character)
            {
                return hoverable;
            }
        }
        return null;
    }

    /// <summary>
    /// Adds the specified hoverable to the hover library. The hoverable's range must not span over multiple lines.
    /// </summary>
    /// <param name="hoverable">IHoverable to add</param>
    /// <exception cref="InvalidDataException"></exception>
    public void Add(IHoverable hoverable)
    {
        //if(hoverable.Range.SpansMultipleLines())
        //{
        //    throw new InvalidDataException("DocumentHoversLibrary does not support hoverables whose ranges span over multiple lines.");
        //}

        int line = hoverable.Range.Start.Line;

        SortedList<int, IHoverable>? lineList = BackingHovers[line];

        if (lineList is null)
        {
            lineList = new();
            BackingHovers[line] = lineList;
        }

        // An edge case exists where if a macro expands a macro to two instances they will collide. This prevents that being an issue.
        // We cannot assume there exists a one-to-one mapping between unique macro uses and the amount of expansions they produce.
        if(!lineList.ContainsKey(hoverable.Range.Start.Character))
        {
            lineList.Add(hoverable.Range.Start.Character, hoverable);
        }
    }
}
