using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

internal static class LexTestHelper
{
    public static LexResult Lex(string source)
    {
        return Lexer.Lex(SourceText.From(source));
    }

    /// <summary>All non-trivia tokens, excluding the trailing EndOfFile.</summary>
    public static List<Token> SignificantTokens(string source)
    {
        LexResult result = Lex(source);
        List<Token> significant = [];

        foreach ( Token token in result.Tokens )
        {
            if ( !token.IsTrivia && token.Kind != TokenKind.EndOfFile )
            {
                significant.Add(token);
            }
        }

        return significant;
    }

    /// <summary>Kinds of all non-trivia tokens, excluding EndOfFile.</summary>
    public static List<TokenKind> SignificantKinds(string source)
    {
        List<TokenKind> kinds = [];
        foreach ( Token token in SignificantTokens(source) )
        {
            kinds.Add(token.Kind);
        }

        return kinds;
    }

    /// <summary>Asserts the source lexes to exactly one significant token and returns it.</summary>
    public static Token Single(string source)
    {
        List<Token> tokens = SignificantTokens(source);
        return Assert.Single(tokens);
    }

    /// <summary>Asserts the source lexes to exactly one significant token of the given kind.</summary>
    public static void AssertSingle(string source, TokenKind expected)
    {
        Token token = Single(source);
        Assert.Equal(expected, token.Kind);
        Assert.Equal(source.Trim(), source.Substring(token.Start, token.Length));
    }

    /// <summary>The raw text of a token within its source.</summary>
    public static string TextOf(Token token, string source)
    {
        return source.Substring(token.Start, token.Length);
    }
}
