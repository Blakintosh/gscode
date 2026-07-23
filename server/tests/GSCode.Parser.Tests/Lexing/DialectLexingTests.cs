using System.Linq;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

/// <summary>
/// Literal forms that only some dialects have. Hash strings (<c>#"precached"</c>) are a Treyarch
/// feature from BO1; the Infinity Ward games and pre-BO1 Treyarch titles do not have them, so
/// <c>#"…"</c> there is a stray <c>#</c> followed by an ordinary string. Gated on
/// <see cref="GameProfile.HasHashStrings"/>, so BO3 is unchanged.
/// </summary>
public class DialectLexingTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    private static TokenKind[] Kinds(string source, GameProfile profile)
    {
        return [.. Lexer.Lex(SourceText.From(source), profile).Tokens
            .Where(static token => !token.IsTrivia)
            .Select(static token => token.Kind)];
    }

    [Fact]
    public void BlackOps3LexesAHashString()
    {
        TokenKind[] kinds = Kinds("#\"combat_robot\"", Bo3);

        Assert.Equal(TokenKind.HashString, kinds[0]);
    }

    [Fact]
    public void ADialectWithoutHashStringsSplitsTheHashFromTheString()
    {
        // CoD4 has no hash strings, so #"..." is a bare '#' and a plain string, which the parser
        // then flags -- rather than silently accepting a foreign literal.
        TokenKind[] kinds = Kinds("#\"combat_robot\"", Cod4);

        Assert.Equal(TokenKind.Hash, kinds[0]);
        Assert.Equal(TokenKind.String, kinds[1]);
    }

    [Fact]
    public void ADialectWithoutHashStringsStillLexesDirectives()
    {
        // The gate is only on the string form -- #include and #using_animtree are unaffected.
        Assert.Equal(TokenKind.IncludeDirective, Kinds("#include common_scripts\\utility;", Cod4)[0]);
    }
}
