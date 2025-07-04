
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.Data;

public sealed class DocumentTokensLibrary
{
    private List<Token> TokenList { get; } = new();

    private class TokenKeyComparer : IComparer<Token>
    {
        public int Compare(Token? x, Token? y)
        {
            if (x is null)
            {
                return y is null ? 0 : -1;
            }
            if (y is null)
            {
                return 1;
            }

            int lineComparison = x.Range.Start.Line.CompareTo(y.Range.Start.Line);
            if (lineComparison != 0)
            {
                return lineComparison;
            }
            return x.Range.Start.Character.CompareTo(y.Range.Start.Character);
        }
    }

    private static readonly TokenKeyComparer _tokenKeyComparer = new();

    private readonly Token _searchToken = new(TokenType.Unknown, new(0, 0, 0, 0), "");
    
    /// <summary>
    /// Gets the nearest token at or before the specified position.
    /// </summary>
    /// <param name="location">The target location</param>
    /// <returns>The token at the specified position if it exists, or null otherwise</returns>
    internal Token? Get(Position location)
    {
        // Not ideal, but it'll do the job.
        Token searchToken = _searchToken with { Range = new(location.Line, location.Character, location.Line, location.Character) };

        int index = TokenList.BinarySearch(searchToken, _tokenKeyComparer);

        // It's the complement when not found - negate it and subtract one to get the preceding token in this case.
        if(index < 0)
        {
            index = Math.Max(0, ~index - 1);
        }

        // If the token is not found, return null.
        if(index >= TokenList.Count)
        {
            return null;
        }

        return TokenList[index];
    }

    /// <summary>
    /// Once the token list has finished its final transformations, create an efficient lookup structure.
    /// </summary>
    /// <param name="startToken">The first token in the list</param>
    internal void AddRange(Token startToken)
    {
        // Skip SOF and EOF
        Token currentToken = startToken.Next!;

        while(currentToken.Type != TokenType.Eof)
        {
            TokenList.Add(currentToken);
            currentToken = currentToken.Next!;
        }
    }

    internal IEnumerable<Token> GetAll()
    {
        return TokenList;
    }
}
