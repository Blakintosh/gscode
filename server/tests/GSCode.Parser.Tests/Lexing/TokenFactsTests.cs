using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

/// <summary>
/// <see cref="TokenFacts.IsKeyword"/> is a RANGE check over the enum, which is fast and correct only
/// while every keyword kind stays inside that range. Nothing in the type system enforces that, and
/// the failure mode is silent: a kind added outside the range still lexes as a keyword, so the word
/// stops being an identifier while every consumer of IsKeyword carries on treating it as one.
///
/// That is not hypothetical. `Vararg` was appended after the previous range end, and the result was
/// no hover documentation on `vararg` and `x.vararg` rejected as a field name — with every test and
/// all 7,309 corpus scripts still green, because no stock script does either.
/// </summary>
public class TokenFactsTests
{
    [Fact]
    public void EveryKindTheKeywordTableProducesIsAKeyword()
    {
        foreach ( TokenKind kind in Keywords.AllKeywordKinds )
        {
            Assert.True(
                TokenFacts.IsKeyword(kind),
                $"{kind} is in the keyword table but falls outside TokenFacts.IsKeyword's range. "
                + "Move it inside the Class..Vararg block in TokenKind, or extend the range end.");
        }
    }

    [Theory]
    [InlineData(TokenKind.Identifier)]
    [InlineData(TokenKind.String)]
    [InlineData(TokenKind.Integer)]
    [InlineData(TokenKind.OpenParen)]
    [InlineData(TokenKind.UsingDirective)]
    [InlineData(TokenKind.EndOfFile)]
    public void ThingsThatAreNotKeywords(TokenKind kind)
    {
        // The other half of the range: widening it far enough to swallow a directive or a literal
        // would make the check pass vacuously.
        Assert.False(TokenFacts.IsKeyword(kind));
    }
}
