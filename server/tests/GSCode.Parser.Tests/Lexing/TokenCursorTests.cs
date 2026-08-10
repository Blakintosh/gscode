using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

public class TokenCursorTests
{
    private static TokenCursor CursorOver(string source)
    {
        return new TokenCursor(Lexer.Lex(SourceText.From(source)).Tokens);
    }

    [Fact]
    public void Cursor_SkipsLeadingTrivia()
    {
        TokenCursor cursor = CursorOver("  // comment\n  foo");
        Assert.Equal(TokenKind.Identifier, cursor.Kind);
    }

    [Fact]
    public void Advance_SkipsInterleavedTrivia()
    {
        TokenCursor cursor = CursorOver("a /* c */ = // x\n 1;");

        Assert.Equal(TokenKind.Identifier, cursor.Kind);
        cursor.Advance();
        Assert.Equal(TokenKind.Assign, cursor.Kind);
        cursor.Advance();
        Assert.Equal(TokenKind.Integer, cursor.Kind);
        cursor.Advance();
        Assert.Equal(TokenKind.Semicolon, cursor.Kind);
        cursor.Advance();
        Assert.Equal(TokenKind.EndOfFile, cursor.Kind);
    }

    [Fact]
    public void Advance_AtEndOfFile_StaysPut()
    {
        TokenCursor cursor = CursorOver("");
        Assert.Equal(TokenKind.EndOfFile, cursor.Kind);

        cursor.Advance();
        Assert.Equal(TokenKind.EndOfFile, cursor.Kind);
    }

    [Fact]
    public void Peek_LooksAheadPastTrivia()
    {
        TokenCursor cursor = CursorOver("a /* trivia */ b c");

        Assert.Equal(TokenKind.Identifier, cursor.Peek(0).Kind);
        Assert.Equal(TokenKind.Identifier, cursor.Peek(1).Kind);
        Assert.Equal(TokenKind.Identifier, cursor.Peek(2).Kind);
        Assert.Equal(TokenKind.EndOfFile, cursor.Peek(3).Kind);
        Assert.Equal(TokenKind.EndOfFile, cursor.Peek(99).Kind);

        // Peeking never moves the cursor.
        Assert.Equal(TokenKind.Identifier, cursor.Kind);
    }
}
