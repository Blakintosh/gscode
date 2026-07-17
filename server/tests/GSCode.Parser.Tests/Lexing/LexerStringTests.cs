using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

public class LexerStringTests
{
    [Fact]
    public void Lex_PlainString()
    {
        Token token = LexTestHelper.Single("\"hello world\"");
        Assert.Equal(TokenKind.String, token.Kind);
    }

    [Fact]
    public void Lex_StringWithEscapedQuote()
    {
        Token token = LexTestHelper.Single("\"say \\\"hi\\\" now\"");
        Assert.Equal(TokenKind.String, token.Kind);
        Assert.Equal(16, token.Length);
    }

    [Fact]
    public void Lex_LocalizedString()
    {
        Token token = LexTestHelper.Single("&\"MENU_LABEL\"");
        Assert.Equal(TokenKind.LocalizedString, token.Kind);
        Assert.Equal(13, token.Length);
    }

    [Fact]
    public void Lex_HashString()
    {
        Token token = LexTestHelper.Single("#\"hash_value\"");
        Assert.Equal(TokenKind.HashString, token.Kind);
        Assert.Equal(13, token.Length);
    }

    [Fact]
    public void Lex_UnterminatedString_TokenPlusDiagnostic()
    {
        LexResult result = LexTestHelper.Lex("x = \"broken\ny = 1;");

        // The string token still appears (up to the line break) so downstream stays usable.
        Assert.Contains(result.Tokens, token => token.Kind == TokenKind.String);
        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GscDiagnosticCode.UnterminatedString, diagnostic.Code);

        // Lexing continues normally on the next line.
        Assert.Contains(result.Tokens, token => token.Kind == TokenKind.Semicolon);
    }

    [Fact]
    public void Lex_UnterminatedStringAtEndOfFile_Diagnostic()
    {
        LexResult result = LexTestHelper.Lex("\"never closed");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GscDiagnosticCode.UnterminatedString, diagnostic.Code);
    }

    [Fact]
    public void Lex_StringWithEmoji_RangeCountsUtf16Units()
    {
        // "🙂" is a surrogate pair: 2 UTF-16 units. The token after it must account for
        // that in its character column (LSP positions are UTF-16 code units).
        string source = "\"🙂\";";
        List<Token> tokens = LexTestHelper.SignificantTokens(source);

        Assert.Equal(TokenKind.String, tokens[0].Kind);
        Assert.Equal(4, tokens[0].Length);
        Assert.Equal(TokenKind.Semicolon, tokens[1].Kind);
        Assert.Equal(4, tokens[1].Range.Start.Character);
    }

    [Fact]
    public void Lex_AmpersandWithoutQuote_IsAddressOf()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("&my_function");
        Assert.Equal([TokenKind.Ampersand, TokenKind.Identifier], kinds);
    }
}
