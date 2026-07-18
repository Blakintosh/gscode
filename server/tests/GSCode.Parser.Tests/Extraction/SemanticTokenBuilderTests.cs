using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Extraction;

public class SemanticTokenBuilderTests
{
    private static ImmutableArray<SemanticToken> Build(string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\test.gsc",
            GSCode.Core.Symbols.ScriptLanguage.Gsc,
            SourceText.From(source),
            NullInsertProvider.Instance,
            new NameTable());
        return SemanticTokenBuilder.Build(result);
    }

    private static bool HasTypeOnLine(ImmutableArray<SemanticToken> tokens, int line, SemanticTokenType type)
    {
        return tokens.Any(token => token.Line == line && token.Type == type);
    }

    [Fact]
    public void Keywords_NumbersAndStrings_AreClassified()
    {
        // function (kw) on line 0; wait 0.05 (number) and "hi" (string) inside.
        ImmutableArray<SemanticToken> tokens = Build("function f()\n{\n    wait 0.05;\n    x = \"hi\";\n}\n");

        Assert.True(HasTypeOnLine(tokens, 0, SemanticTokenType.Keyword));
        Assert.True(HasTypeOnLine(tokens, 2, SemanticTokenType.Number));
        Assert.True(HasTypeOnLine(tokens, 3, SemanticTokenType.String));
    }

    [Fact]
    public void FunctionCall_IsClassifiedAsFunction()
    {
        ImmutableArray<SemanticToken> tokens = Build("function caller()\n{\n    helper();\n}\n");

        // "caller" definition (line 0) and "helper" call (line 2) both classify as Function.
        Assert.True(HasTypeOnLine(tokens, 0, SemanticTokenType.Function));
        Assert.True(HasTypeOnLine(tokens, 2, SemanticTokenType.Function));
    }

    [Fact]
    public void FieldAccess_IsProperty_AndMacroUse_IsMacro()
    {
        ImmutableArray<SemanticToken> tokens = Build("#define CAP 5\nfunction f()\n{\n    x = self.health;\n    y = CAP;\n}\n");

        Assert.True(HasTypeOnLine(tokens, 3, SemanticTokenType.Property));
        Assert.True(HasTypeOnLine(tokens, 4, SemanticTokenType.Macro));
    }

    [Fact]
    public void Comments_AreClassified()
    {
        ImmutableArray<SemanticToken> tokens = Build("// header\nfunction f()\n{\n}\n");
        Assert.True(HasTypeOnLine(tokens, 0, SemanticTokenType.Comment));
    }

    [Fact]
    public void TokensAreOrdered()
    {
        ImmutableArray<SemanticToken> tokens = Build("function f()\n{\n    a = 1;\n    b = 2;\n}\n");

        for ( int index = 1; index < tokens.Length; index++ )
        {
            SemanticToken previous = tokens[index - 1];
            SemanticToken current = tokens[index];
            bool ordered = previous.Line < current.Line
                || (previous.Line == current.Line && previous.StartChar <= current.StartChar);
            Assert.True(ordered, $"token {index} out of order");
        }
    }
}
