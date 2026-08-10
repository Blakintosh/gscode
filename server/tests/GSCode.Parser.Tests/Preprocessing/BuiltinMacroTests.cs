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

    /// <summary>The single string token an expansion produced, for the __FUNCTION__ cases below.</summary>
    private static string ExpandedString(string source)
    {
        PreprocessResult result = PreprocessTestHelper.Run(source);
        return Assert.Single(result.Tokens, token => token.Kind == TokenKind.String).Text;
    }

    [Fact]
    public void Function_ExpandsToTheQualifiedNameAsAString()
    {
        // spawner_shared.gsc:517 is the only stock use:
        // `assert( ..., __FUNCTION__ + " only supports actors and vehicles." )`.
        string expanded = ExpandedString(
            "#namespace spawner;\nfunction spawn_think( spawner )\n{\n    x = __FUNCTION__;\n}\n");

        Assert.Equal("\"spawner::spawn_think\"", expanded);
    }

    [Fact]
    public void Function_UsesTheNearestPrecedingFunction()
    {
        string expanded = ExpandedString(
            "#namespace util;\nfunction first()\n{\n}\nfunction second()\n{\n    x = __FUNCTION__;\n}\n");

        Assert.Equal("\"util::second\"", expanded);
    }

    [Fact]
    public void Function_SkipsTheModifiersBeforeTheName()
    {
        string expanded = ExpandedString(
            "#namespace util;\nfunction private hidden()\n{\n    x = __FUNCTION__;\n}\n");

        Assert.Equal("\"util::hidden\"", expanded);
    }

    [Fact]
    public void Function_FallsBackToTheFileStemWhenNoNamespaceIsDeclared()
    {
        // The dialect's own fallback for a file that declares none.
        string expanded = ExpandedString("function run()\n{\n    x = __FUNCTION__;\n}\n");

        Assert.Equal("\"" + PreprocessTestHelper.RootStem + "::run\"", expanded);
    }

    [Fact]
    public void Function_OutsideAnyFunction_IsJustTheNamespace()
    {
        // Nothing to qualify, so a trailing "::" would read like a name went missing.
        string expanded = ExpandedString("#namespace util;\nx = __FUNCTION__;\n");

        Assert.Equal("\"util\"", expanded);
    }

    [Fact]
    public void Function_InsideAMacroBody_IsNotExpanded()
    {
        // Pinned as a KNOWN LIMIT, not as desirable behaviour. Macro bodies are expanded by a
        // separate path that never consults the builtin table, so __LINE__ and __FILE__ have always
        // behaved this way too. Lifting it means carrying the INVOCATION site through expansion —
        // the body's own position is in another token array and answers the wrong question — and no
        // stock script writes any of the three inside a macro.
        PreprocessResult result = PreprocessTestHelper.Run(
            "#namespace util;\n#define NAME __FUNCTION__\nfunction run()\n{\n    x = NAME;\n}\n");

        Assert.DoesNotContain(result.Tokens, token => token.Kind == TokenKind.String);
    }

    [Fact]
    public void Function_InsideAClassMethod_UsesTheNamespaceNotTheClass()
    {
        // Pinned as the deliberate choice it is: no stock script writes __FUNCTION__ inside a class,
        // so there is no evidence the compiler names the class, and inventing one would be a guess.
        string expanded = ExpandedString(
            "#namespace scene;\nclass cScene\n{\n    function play()\n    {\n        x = __FUNCTION__;\n    }\n}\n");

        Assert.Equal("\"scene::play\"", expanded);
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
