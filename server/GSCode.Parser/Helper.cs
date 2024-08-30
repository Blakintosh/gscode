using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser;

internal static class Helper
{
    /// <summary>
    /// Produces a range instance that matches the start and end line and positions provided.
    /// </summary>
    /// <param name="startLine"></param>
    /// <param name="startCharacter"></param>
    /// <param name="endLine"></param>
    /// <param name="endCharacter"></param>
    /// <returns></returns>
    public static Range RangeFrom(int startLine, int startCharacter, int endLine, int endCharacter)
    {
        return new Range
        {
            Start = new Position(startLine, startCharacter),
            End = new Position(endLine, endCharacter)
        };
    }

    /// <summary>
    /// Produces a range instance that matches the start and end positions provided.
    /// </summary>
    /// <param name="start"></param>
    /// <param name="end"></param>
    /// <returns></returns>
    public static Range RangeFrom(Position start, Position end)
    {
        return new Range
        {
            Start = start,
            End = end
        };
    }
}
