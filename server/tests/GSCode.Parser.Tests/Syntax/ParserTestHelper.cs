using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

internal static class ParserTestHelper
{
    /// <summary>Runs the full pipeline (lex → preprocess → parse) over a snippet, for a dialect.</summary>
    public static ParseTree Parse(string source, GameProfile? profile = null)
    {
        GameProfile game = profile ?? GameProfile.BlackOps3;
        SourceText text = SourceText.From(source);
        LexResult lexed = Lexer.Lex(text, game);
        PreprocessResult preprocessed = Preprocessor.Process(
            @"c:\work\scripts\test.gsc", lexed.Tokens, text, NullInsertProvider.Instance, new NameTable());
        return Parser.Syntax.Parser.Parse(preprocessed.Tokens, game);
    }

    /// <summary>S-expression of the whole script node.</summary>
    public static string PrintScript(string source)
    {
        return AstPrinter.Print(Parse(source).Root);
    }

    /// <summary>S-expression of the body of "function test() { ... }" wrapping the snippet.</summary>
    public static string PrintBody(string statements)
    {
        ParseTree tree = Parse("function test()\n{\n" + statements + "\n}");
        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        return AstPrinter.Print(function.Body);
    }

    /// <summary>S-expression of a single expression statement's expression.</summary>
    public static string PrintExpr(string expression)
    {
        string body = PrintBody(expression + ";");
        Assert.StartsWith("(block ", body, StringComparison.Ordinal);
        return body["(block ".Length..^1];
    }
}
