using System.Collections.Immutable;

namespace GSCode.Parser.Lexing;

/// <summary>
/// The parser's view over the token stream: skips trivia transparently and never runs
/// past EndOfFile. The raw array (with trivia) stays available for formatter/semantic use.
/// </summary>
public struct TokenCursor
{
    private readonly ImmutableArray<Token> _tokens;
    private int _index;

    public TokenCursor(ImmutableArray<Token> tokens)
    {
        _tokens = tokens;
        _index = 0;
        SkipTrivia();
    }

    /// <summary>The current (non-trivia) token.</summary>
    public readonly Token Current
    {
        get { return _tokens[_index]; }
    }

    /// <summary>Kind of the current token.</summary>
    public readonly TokenKind Kind
    {
        get { return _tokens[_index].Kind; }
    }

    /// <summary>Raw index of the current token in the full (trivia-included) array.</summary>
    public readonly int Index
    {
        get { return _index; }
    }

    /// <summary>Moves to the next non-trivia token; stays put once at EndOfFile.</summary>
    public void Advance()
    {
        if ( Kind == TokenKind.EndOfFile )
        {
            return;
        }

        _index++;
        SkipTrivia();
    }

    /// <summary>Looks ahead by N non-trivia tokens without moving (Peek(0) == Current).</summary>
    public readonly Token Peek(int lookahead)
    {
        int index = _index;
        int remaining = lookahead;

        while ( remaining > 0 && _tokens[index].Kind != TokenKind.EndOfFile )
        {
            index++;
            while ( _tokens[index].IsTrivia )
            {
                index++;
            }

            remaining--;
        }

        return _tokens[index];
    }

    private void SkipTrivia()
    {
        // The stream always ends with a non-trivia EndOfFile token, so this terminates.
        while ( _tokens[_index].IsTrivia )
        {
            _index++;
        }
    }
}
