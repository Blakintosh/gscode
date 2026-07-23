using System.Linq;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

/// <summary>
/// Keywords are per-dialect. A word is only a keyword where the game actually has the construct:
/// <c>foreach</c> before MW2, <c>function</c>/<c>class</c>/<c>var</c>/<c>do</c> in the Infinity Ward
/// games all stay ordinary identifiers, so a script can use them as names. BO3 has every keyword,
/// so its lexing is unchanged.
/// </summary>
public class KeywordDialectTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    private static TokenKind FirstKind(string word, GameProfile profile)
    {
        return Lexer.Lex(SourceText.From(word), profile).Tokens.First(static token => !token.IsTrivia).Kind;
    }

    [Theory]
    [InlineData("function", TokenKind.Function)]
    [InlineData("class", TokenKind.Class)]
    [InlineData("var", TokenKind.Var)]
    [InlineData("new", TokenKind.New)]
    [InlineData("foreach", TokenKind.Foreach)]
    [InlineData("do", TokenKind.Do)]
    public void BlackOps3_KeepsEveryKeyword(string word, TokenKind expected)
    {
        Assert.Equal(expected, FirstKind(word, Bo3));
    }

    [Theory]
    [InlineData("function")]
    [InlineData("class")]
    [InlineData("var")]
    [InlineData("new")]
    [InlineData("foreach")]
    [InlineData("do")]
    public void Cod4_HasNoneOfThoseKeywords_TheyAreIdentifiers(string word)
    {
        Assert.Equal(TokenKind.Identifier, FirstKind(word, Cod4));
    }

    [Fact]
    public void ForeachArrivesInMw2_ButDoWhileAndClassesDoNot()
    {
        // MW2 has foreach (2009) but not do-while or classes.
        Assert.Equal(TokenKind.Foreach, FirstKind("foreach", Mw2));
        Assert.Equal(TokenKind.Identifier, FirstKind("do", Mw2));
        Assert.Equal(TokenKind.Identifier, FirstKind("class", Mw2));
    }

    [Theory]
    [InlineData("if", TokenKind.If)]
    [InlineData("else", TokenKind.Else)]
    [InlineData("for", TokenKind.For)]
    [InlineData("while", TokenKind.While)]
    [InlineData("switch", TokenKind.Switch)]
    [InlineData("return", TokenKind.Return)]
    [InlineData("break", TokenKind.Break)]
    [InlineData("continue", TokenKind.Continue)]
    [InlineData("thread", TokenKind.Thread)]
    [InlineData("waittill", TokenKind.WaitTill)]
    public void SharedKeywordsExistInEveryGame(string word, TokenKind expected)
    {
        // The control-flow and event baseline is present everywhere, so it is never gated.
        Assert.Equal(expected, FirstKind(word, Cod4));
        Assert.Equal(expected, FirstKind(word, Bo3));
    }

    [Fact]
    public void AKeywordlessDialectCanUseTheWordAsAName()
    {
        // `foreach = 1;` is a valid statement in CoD4 -- foreach is just a variable there.
        TokenKind[] tokens = [.. Lexer.Lex(SourceText.From("foreach = 1;"), Cod4).Tokens
            .Where(static token => !token.IsTrivia)
            .Select(static token => token.Kind)];

        Assert.Equal(TokenKind.Identifier, tokens[0]);
        Assert.Equal(TokenKind.Assign, tokens[1]);
    }
}
