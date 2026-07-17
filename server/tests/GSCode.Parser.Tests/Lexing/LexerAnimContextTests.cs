using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

/// <summary>
/// %word is an animation reference only where no operand can sit to its left
/// (after = ( , : ? return, or at start of file); otherwise % is modulo.
/// </summary>
public class LexerAnimContextTests
{
    [Theory]
    [InlineData("x = %anim_run;")]
    [InlineData("play(%anim_run)")]
    [InlineData("play(a, %anim_run)")]
    [InlineData("x = b ? %anim_run : c;")]
    [InlineData("return %anim_run;")]
    public void Lex_AnimReference_InAnimContexts(string source)
    {
        Assert.Contains(TokenKind.AnimReference, LexTestHelper.SignificantKinds(source));
    }

    [Fact]
    public void Lex_AnimReference_AtStartOfFile()
    {
        Assert.Equal(TokenKind.AnimReference, LexTestHelper.SignificantKinds("%anim_run")[0]);
    }

    [Fact]
    public void Lex_AnimReference_AfterTernaryColon()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("x = b ? c : %anim_idle;");
        Assert.Contains(TokenKind.AnimReference, kinds);
    }

    [Theory]
    [InlineData("a % b")]
    [InlineData("10 % 3")]
    [InlineData("(x) % y")]
    [InlineData("arr[i] % y")]
    public void Lex_Modulo_AfterOperands(string source)
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds(source);
        Assert.Contains(TokenKind.Percent, kinds);
        Assert.DoesNotContain(TokenKind.AnimReference, kinds);
    }

    [Fact]
    public void Lex_Modulo_AfterCompoundAssign()
    {
        // x += 10 % 3 — the 10 is the left operand, so % is modulo.
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("x += 10 % 3;");
        Assert.Contains(TokenKind.Percent, kinds);
        Assert.DoesNotContain(TokenKind.AnimReference, kinds);
    }

    [Fact]
    public void Lex_PercentWithoutWord_IsModuloEvenInAnimContext()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("x = % 2;");
        Assert.Contains(TokenKind.Percent, kinds);
    }
}
