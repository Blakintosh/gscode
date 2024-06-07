using GSCode.Data.Models;

namespace GSCode.Lexer.Types.Interfaces
{
    internal interface ISingleLineFactory
    {
        public bool HasMatch(ReadOnlySpan<char> lineSpan, int line, int currentIndex, out Token matchedToken);
    }
}
