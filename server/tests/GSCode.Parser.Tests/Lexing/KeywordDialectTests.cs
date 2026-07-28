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

    [Fact]
    public void ChildThreadAndCall_AreMw2KeywordsButBo3AndCod4Identifiers()
    {
        // childthread and call are the Infinity Ward line's MW2 additions (their own token kinds).
        Assert.Equal(TokenKind.ChildThread, FirstKind("childthread", Mw2));
        Assert.Equal(TokenKind.Call, FirstKind("call", Mw2));

        // BO3 uses neither (its corpus uses `call` as an ordinary identifier ~69x), so there they
        // stay identifiers — which is exactly what keeps BO3 lexing byte-identical.
        Assert.Equal(TokenKind.Identifier, FirstKind("childthread", Bo3));
        Assert.Equal(TokenKind.Identifier, FirstKind("call", Bo3));

        // And the base dialect (CoD4) has neither.
        Assert.Equal(TokenKind.Identifier, FirstKind("childthread", Cod4));
        Assert.Equal(TokenKind.Identifier, FirstKind("call", Cod4));
    }

    [Fact]
    public void Const_IsBlackOps3Only()
    {
        // const is a BO3 addition; the earlier games have no file-scope const keyword, so the word is
        // an ordinary identifier there.
        Assert.Equal(TokenKind.Const, FirstKind("const", Bo3));
        Assert.Equal(TokenKind.Identifier, FirstKind("const", Mw2));
        Assert.Equal(TokenKind.Identifier, FirstKind("const", Cod4));
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
