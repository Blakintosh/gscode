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
    public void Keywords_NumbersAndStrings_AreLeftToTheGrammar()
    {
        // These are LEXICAL categories: a string is a string and `function` is a keyword by their
        // spelling alone, with no surrounding context that could change the answer. That is exactly
        // what a grammar settles, and the grammar has already coloured the file by the time the
        // server's tokens arrive — so emitting them again only repainted the same words in a
        // different shade, which is the startup flicker.
        //
        // Standing down cannot cost correctness either, because a semantic token can only add or
        // repaint and never suppress: where the two agree only the shade changed, and where they
        // disagree the grammar's colour stood regardless.
        ImmutableArray<SemanticToken> tokens = Build("function f()\n{\n    wait 0.05;\n    x = \"hi\";\n}\n");

        Assert.DoesNotContain(tokens, token => token.Type == SemanticTokenType.Keyword);
        Assert.DoesNotContain(tokens, token => token.Type == SemanticTokenType.Number);
        Assert.DoesNotContain(tokens, token => token.Type == SemanticTokenType.String);
    }

    [Fact]
    public void OnlyIdentifiersAreClassified()
    {
        // What is left is the one question a grammar genuinely cannot answer: what an IDENTIFIER
        // means. Whether `foo` is a function, a macro, a parameter or a field is a fact about the
        // workspace rather than about the characters.
        ImmutableArray<SemanticToken> tokens = Build(
            "#define CAP 5\nfunction f( p )\n{\n    wait 0.05;\n    x = self.health;\n    y = CAP;\n    g( p );\n}\n");

        Assert.NotEmpty(tokens);
        Assert.All(tokens, token => Assert.Contains(token.Type, new[]
        {
            SemanticTokenType.Function,
            SemanticTokenType.Macro,
            SemanticTokenType.Parameter,
            SemanticTokenType.Variable,
            SemanticTokenType.Property,
            SemanticTokenType.Namespace,
            SemanticTokenType.Type,
        }));
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
    public void Comments_AreLeftToTheGrammar()
    {
        // A semantic token OVERRIDES the TextMate scopes across the range it covers, so emitting
        // one for a comment flattened everything the grammar colours inside it — which for a
        // /@ … @/ block is its descriptors, argument names and types, and is why ScriptDoc rendered
        // as one block of comment grey.
        //
        // Nothing is lost: a comment is a comment whatever the surrounding code means, which makes
        // it the one thing a grammar answers completely on its own.
        ImmutableArray<SemanticToken> lineComment = Build("// header\nfunction f()\n{\n}\n");
        Assert.False(HasTypeOnLine(lineComment, 0, SemanticTokenType.Comment));

        ImmutableArray<SemanticToken> docComment = Build("/@\n\"Name: f()\"\n@/\nfunction f()\n{\n}\n");
        Assert.DoesNotContain(docComment, token => token.Type == SemanticTokenType.Comment);
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
