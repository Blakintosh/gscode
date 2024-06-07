using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Data;

public static class RangeHelper
{
    public static Range From(int startLine, int startCharacter, int endLine, int endCharacter)
    {
        return new Range
        {
            Start = new Position(startLine, startCharacter),
            End = new Position(endLine, endCharacter)
        };
    }
    public static Range From(Position start, Position end)
    {
        return new Range
        {
            Start = start,
            End = end
        };
    }
}
