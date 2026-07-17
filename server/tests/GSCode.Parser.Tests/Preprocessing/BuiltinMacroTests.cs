using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

public class BuiltinMacroTests
{
    [Fact]
    public void Line_ExpandsToOneBasedLineNumber()
    {
        PreprocessResult result = PreprocessTestHelper.Run("\n\nx = __LINE__;");

        PToken expanded = Assert.Single(result.Tokens, token => token.Kind == TokenKind.Integer);
        Assert.Equal("3", expanded.Text);
    }

    [Fact]
    public void File_ExpandsToStringWithRootPath()
    {
        PreprocessResult result = PreprocessTestHelper.Run("x = __FILE__;");

        PToken expanded = Assert.Single(result.Tokens, token => token.Kind == TokenKind.String);
        Assert.Contains(PreprocessTestHelper.RootPath, expanded.Text);
    }

    [Fact]
    public void FastFile_ExpandsToPlaceholderIdentifier()
    {
        // The fastfile name only exists at link time; a placeholder keeps parsing sane.
        PreprocessResult result = PreprocessTestHelper.Run("x = FASTFILE;");

        Assert.Contains(result.Tokens, token => token.Kind == TokenKind.Identifier && token.Text == "__fastfile__");
    }

    [Fact]
    public void PassthroughDirectives_SurviveToParseStream()
    {
        // #using/#namespace/#precache/#animtree are the parser's business, not ours.
        PreprocessResult result = PreprocessTestHelper.Run("#using scripts\\shared\\util;\n#namespace foo;\n#precache(\"string\", \"HINT\");");

        List<TokenKind> kinds = PreprocessTestHelper.Kinds(result);
        Assert.Contains(TokenKind.UsingDirective, kinds);
        Assert.Contains(TokenKind.NamespaceDirective, kinds);
        Assert.Contains(TokenKind.PrecacheDirective, kinds);
    }

    [Fact]
    public void Trivia_NeverReachesParseStream()
    {
        PreprocessResult result = PreprocessTestHelper.Run("a = 1; // comment\n/* block */ b = 2;");

        foreach ( PToken token in result.Tokens )
        {
            Assert.False(token.Kind is TokenKind.Whitespace or TokenKind.Newline or TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment);
        }
    }
}
