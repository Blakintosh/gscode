using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

public class LexerDirectiveTests
{
    [Theory]
    [InlineData("#using", TokenKind.UsingDirective)]
    [InlineData("#insert", TokenKind.InsertDirective)]
    [InlineData("#define", TokenKind.DefineDirective)]
    [InlineData("#namespace", TokenKind.NamespaceDirective)]
    [InlineData("#precache", TokenKind.PrecacheDirective)]
    [InlineData("#using_animtree", TokenKind.UsingAnimTreeDirective)]
    [InlineData("#animtree", TokenKind.AnimTreeDirective)]
    [InlineData("#if", TokenKind.IfDirective)]
    [InlineData("#elif", TokenKind.ElifDirective)]
    [InlineData("#else", TokenKind.ElseDirective)]
    [InlineData("#endif", TokenKind.EndifDirective)]
    public void Lex_KnownDirectives(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Fact]
    public void Lex_DirectivesAreCaseSensitive()
    {
        // The engine only accepts lowercase directives; #USING is unknown.
        LexResult result = LexTestHelper.Lex("#USING");

        Assert.Equal(TokenKind.Error, result.Tokens[0].Kind);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GscDiagnosticCode.UnknownDirective, diagnostic.Code);
    }

    [Fact]
    public void Lex_UnknownDirective_WholeWordMatch()
    {
        // "#iffoo" must NOT lex as "#if" + "foo" — whole-word matching catches the typo.
        LexResult result = LexTestHelper.Lex("#iffoo");

        Token error = Assert.Single(LexTestHelper.SignificantTokens("#iffoo"));
        Assert.Equal(TokenKind.Error, error.Kind);
        Assert.Equal(6, error.Length);
        Assert.Contains("#iffoo", Assert.Single(result.Diagnostics).Message);
    }

    [Fact]
    public void Lex_UsingStatement_FullShape()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds(@"#using scripts\shared\util_shared;");

        Assert.Equal(TokenKind.UsingDirective, kinds[0]);
        Assert.Equal(TokenKind.Semicolon, kinds[^1]);
        Assert.Contains(TokenKind.Backslash, kinds);
    }

    [Fact]
    public void Lex_BareHash_IsHashToken()
    {
        LexTestHelper.AssertSingle("#", TokenKind.Hash);
    }

    [Fact]
    public void Lex_DevBlockClose_BeforeDirectiveMatching()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("#/");
        Assert.Equal([TokenKind.DevBlockClose], kinds);
    }

    [Fact]
    public void Lex_DefineWithLineContinuation()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("#define FOO a \\\n b");

        Assert.Equal(TokenKind.DefineDirective, kinds[0]);
        Assert.Contains(TokenKind.Backslash, kinds);
    }
}
