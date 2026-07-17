using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

public class LexerTriviaTests
{
    private static List<TokenKind> AllKinds(string source)
    {
        List<TokenKind> kinds = [];
        foreach ( Token token in LexTestHelper.Lex(source).Tokens )
        {
            kinds.Add(token.Kind);
        }

        return kinds;
    }

    [Fact]
    public void Lex_WhitespaceRun_SingleToken()
    {
        List<TokenKind> kinds = AllKinds("a  \t b");
        Assert.Equal([TokenKind.Identifier, TokenKind.Whitespace, TokenKind.Identifier, TokenKind.EndOfFile], kinds);
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    public void Lex_LineBreakStyles_OneNewlineToken(string source)
    {
        List<TokenKind> kinds = AllKinds(source);
        Assert.Equal([TokenKind.Identifier, TokenKind.Newline, TokenKind.Identifier, TokenKind.EndOfFile], kinds);
    }

    [Fact]
    public void Lex_LineComment_ExcludesNewline()
    {
        List<TokenKind> kinds = AllKinds("a // note\nb");
        Assert.Equal(
            [TokenKind.Identifier, TokenKind.Whitespace, TokenKind.LineComment, TokenKind.Newline, TokenKind.Identifier, TokenKind.EndOfFile],
            kinds);
    }

    [Fact]
    public void Lex_BlockComment_SpansLines()
    {
        LexResult result = LexTestHelper.Lex("a /* one\ntwo */ b");
        Token comment = Assert.Single(result.Tokens, token => token.Kind == TokenKind.BlockComment);

        Assert.Equal(0, comment.Range.Start.Line);
        Assert.Equal(1, comment.Range.End.Line);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Lex_DocComment_SingleToken()
    {
        LexResult result = LexTestHelper.Lex("/@ Name: foo @/ function");
        Assert.Contains(result.Tokens, token => token.Kind == TokenKind.DocComment);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Lex_UnterminatedBlockComment_Diagnostic()
    {
        LexResult result = LexTestHelper.Lex("a /* never closed");
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GscDiagnosticCode.UnterminatedBlockComment, diagnostic.Code);
    }

    [Fact]
    public void Lex_UnterminatedDocComment_Diagnostic()
    {
        LexResult result = LexTestHelper.Lex("/@ never closed");
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GscDiagnosticCode.UnterminatedDocComment, diagnostic.Code);
    }

    [Fact]
    public void Lex_DevBlockDelimiters()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("/# x = 1; #/");
        Assert.Equal(
            [TokenKind.DevBlockOpen, TokenKind.Identifier, TokenKind.Assign, TokenKind.Integer, TokenKind.Semicolon, TokenKind.DevBlockClose],
            kinds);
    }

    [Fact]
    public void Lex_MultilineToken_RangeIsCorrect()
    {
        LexResult result = LexTestHelper.Lex("/* a\nb\nc */x");
        Token comment = result.Tokens[0];

        Assert.Equal(0, comment.Range.Start.Line);
        Assert.Equal(0, comment.Range.Start.Character);
        Assert.Equal(2, comment.Range.End.Line);
        Assert.Equal(4, comment.Range.End.Character);

        Token identifier = result.Tokens[1];
        Assert.Equal(2, identifier.Range.Start.Line);
        Assert.Equal(4, identifier.Range.Start.Character);
    }
}
