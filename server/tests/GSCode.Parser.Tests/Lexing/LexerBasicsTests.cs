using GSCode.Parser.Lexing;
using Xunit;

namespace GSCode.Parser.Tests.Lexing;

public class LexerBasicsTests
{
    [Theory]
    [InlineData("myVar", TokenKind.Identifier)]
    [InlineData("_underscore", TokenKind.Identifier)]
    [InlineData("var2", TokenKind.Identifier)]
    [InlineData("self", TokenKind.Identifier)]
    [InlineData("level", TokenKind.Identifier)]
    [InlineData("game", TokenKind.Identifier)]
    [InlineData("world", TokenKind.Identifier)]
    [InlineData("gettime", TokenKind.Identifier)] // a builtin, not a keyword — lexes as an identifier
    public void Lex_Identifiers_IncludingGlobals(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Theory]
    [InlineData("123", TokenKind.Integer)]
    [InlineData("0", TokenKind.Integer)]
    [InlineData("3.14", TokenKind.Float)]
    [InlineData(".5", TokenKind.Float)]
    [InlineData("0xFF", TokenKind.Hex)]
    [InlineData("0x1a2B", TokenKind.Hex)]
    public void Lex_NumericLiterals(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Fact]
    public void Lex_BareZeroX_IsNotHex()
    {
        // "0x" with no hex digits falls back to integer 0 followed by identifier x.
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("0x");
        Assert.Equal([TokenKind.Integer, TokenKind.Identifier], kinds);
    }

    [Fact]
    public void Lex_TrailingDot_IsIntegerThenDot()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("1.size");
        Assert.Equal([TokenKind.Integer, TokenKind.Dot, TokenKind.Identifier], kinds);
    }

    [Theory]
    [InlineData("function", TokenKind.Function)]
    [InlineData("Function", TokenKind.Function)]
    [InlineData("FUNCTION", TokenKind.Function)]
    [InlineData("Do", TokenKind.Do)]
    [InlineData("Break", TokenKind.Break)]
    [InlineData("waittill", TokenKind.WaitTill)]
    [InlineData("WaitTillFrameEnd", TokenKind.WaitTillFrameEnd)]
    [InlineData("isdefined", TokenKind.IsDefined)]
    [InlineData("IsDefined", TokenKind.IsDefined)]
    [InlineData("undefined", TokenKind.Undefined)]
    [InlineData("true", TokenKind.True)]
    [InlineData("False", TokenKind.False)]
    [InlineData("constructor", TokenKind.Constructor)]
    [InlineData("autoexec", TokenKind.Autoexec)]
    [InlineData("private", TokenKind.Private)]
    [InlineData("vectorscale", TokenKind.VectorScale)]
    public void Lex_Keywords_CaseInsensitive(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Theory]
    [InlineData("+", TokenKind.Plus)]
    [InlineData("-", TokenKind.Minus)]
    [InlineData("*", TokenKind.Star)]
    [InlineData("/", TokenKind.Slash)]
    [InlineData("=", TokenKind.Assign)]
    [InlineData("==", TokenKind.EqualsEquals)]
    [InlineData("===", TokenKind.StrictEquals)]
    [InlineData("!", TokenKind.Bang)]
    [InlineData("!=", TokenKind.NotEquals)]
    [InlineData("!==", TokenKind.StrictNotEquals)]
    [InlineData("&&", TokenKind.LogicalAnd)]
    [InlineData("||", TokenKind.LogicalOr)]
    [InlineData("&", TokenKind.Ampersand)]
    [InlineData("|", TokenKind.Pipe)]
    [InlineData("^", TokenKind.Caret)]
    [InlineData("~", TokenKind.Tilde)]
    [InlineData("<", TokenKind.LessThan)]
    [InlineData("<=", TokenKind.LessThanEquals)]
    [InlineData("<<", TokenKind.ShiftLeft)]
    [InlineData("<<=", TokenKind.ShiftLeftAssign)]
    [InlineData(">", TokenKind.GreaterThan)]
    [InlineData(">=", TokenKind.GreaterThanEquals)]
    [InlineData(">>", TokenKind.ShiftRight)]
    [InlineData(">>=", TokenKind.ShiftRightAssign)]
    [InlineData("++", TokenKind.PlusPlus)]
    [InlineData("--", TokenKind.MinusMinus)]
    [InlineData("+=", TokenKind.PlusAssign)]
    [InlineData("-=", TokenKind.MinusAssign)]
    [InlineData("*=", TokenKind.StarAssign)]
    [InlineData("/=", TokenKind.SlashAssign)]
    [InlineData("%=", TokenKind.PercentAssign)]
    [InlineData("&=", TokenKind.AmpersandAssign)]
    [InlineData("|=", TokenKind.PipeAssign)]
    [InlineData("^=", TokenKind.CaretAssign)]
    [InlineData("->", TokenKind.Arrow)]
    [InlineData("::", TokenKind.ScopeResolution)]
    [InlineData(":", TokenKind.Colon)]
    [InlineData("?", TokenKind.QuestionMark)]
    [InlineData("...", TokenKind.Ellipsis)]
    [InlineData("$", TokenKind.Dollar)]
    [InlineData("\\", TokenKind.Backslash)]
    public void Lex_OperatorsAndPunctuation(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Theory]
    [InlineData("(", TokenKind.OpenParen)]
    [InlineData(")", TokenKind.CloseParen)]
    [InlineData("[", TokenKind.OpenBracket)]
    [InlineData("]", TokenKind.CloseBracket)]
    [InlineData("{", TokenKind.OpenBrace)]
    [InlineData("}", TokenKind.CloseBrace)]
    [InlineData(";", TokenKind.Semicolon)]
    [InlineData(",", TokenKind.Comma)]
    [InlineData(".", TokenKind.Dot)]
    public void Lex_Delimiters(string source, TokenKind expected)
    {
        LexTestHelper.AssertSingle(source, expected);
    }

    [Fact]
    public void Lex_MemberAccess_ThreeTokens()
    {
        List<Token> tokens = LexTestHelper.SignificantTokens("self.field");

        Assert.Equal(3, tokens.Count);
        Assert.Equal(TokenKind.Identifier, tokens[0].Kind);
        Assert.Equal(TokenKind.Dot, tokens[1].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
        Assert.Equal("self", LexTestHelper.TextOf(tokens[0], "self.field"));
        Assert.Equal("field", LexTestHelper.TextOf(tokens[2], "self.field"));
    }

    [Fact]
    public void Lex_DoubleBrackets_AreAdjacentSingleBrackets()
    {
        // [[ and ]] are deliberately two adjacent bracket tokens (the parser checks
        // adjacency), so nested indexers like a[b[1]] lex unambiguously.
        List<Token> tokens = LexTestHelper.SignificantTokens("[[obj]]");

        Assert.Equal(TokenKind.OpenBracket, tokens[0].Kind);
        Assert.Equal(TokenKind.OpenBracket, tokens[1].Kind);
        Assert.Equal(tokens[0].End, tokens[1].Start);
        Assert.Equal(TokenKind.CloseBracket, tokens[3].Kind);
        Assert.Equal(TokenKind.CloseBracket, tokens[4].Kind);
        Assert.Equal(tokens[3].End, tokens[4].Start);
    }

    [Fact]
    public void Lex_NestedIndexer_ProducesFourSingleBrackets()
    {
        List<TokenKind> kinds = LexTestHelper.SignificantKinds("a[b[1]]");

        Assert.Equal(
            [
                TokenKind.Identifier,
                TokenKind.OpenBracket,
                TokenKind.Identifier,
                TokenKind.OpenBracket,
                TokenKind.Integer,
                TokenKind.CloseBracket,
                TokenKind.CloseBracket,
            ],
            kinds);
    }

    [Fact]
    public void Lex_UnexpectedCharacter_ErrorTokenAndDiagnostic()
    {
        LexResult result = LexTestHelper.Lex("a ` b");

        Assert.Contains(result.Tokens, token => token.Kind == TokenKind.Error);
        Xunit.Assert.Single(result.Diagnostics);
        Assert.Equal(GSCode.Core.Diagnostics.GscDiagnosticCode.UnexpectedCharacter, result.Diagnostics[0].Code);
    }
}
