using System.Linq;
using GSCode.Core;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// The first dialect fork: whether a function declaration needs the <c>function</c> keyword. BO3
/// requires it; the Infinity Ward games write a bare <c>name( … ) { … }</c>. Gated on
/// <see cref="GameProfile.HasFunctionKeyword"/>, so BO3 is unchanged and only the keyword-less
/// dialects accept the bare form.
/// </summary>
public class DialectDeclarationTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    [Fact]
    public void AKeywordlessDialectAcceptsABareFunction()
    {
        ParseTree tree = ParserTestHelper.Parse("main()\n{\n}\n", Cod4);

        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        Assert.Equal("main", function.NameToken.Text);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void ABareFunctionKeepsItsParametersAndBody()
    {
        ParseTree tree = ParserTestHelper.Parse("foo( a, b )\n{\n\tx = 1;\n}\n", Cod4);

        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        Assert.Equal("foo", function.NameToken.Text);
        Assert.Equal(2, function.Parameters.Length);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void SeveralBareFunctionsParseInOrder()
    {
        ParseTree tree = ParserTestHelper.Parse("a()\n{\n}\nb()\n{\n}\n", Cod4);

        FunctionNode[] functions = [.. tree.Root.Elements.OfType<FunctionNode>()];
        Assert.Equal(new[] { "a", "b" }, functions.Select(f => f.NameToken.Text).ToArray());
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3StillRequiresTheFunctionKeyword()
    {
        // The same bare form is NOT a declaration in BO3 -- it needs `function`.
        ParseTree tree = ParserTestHelper.Parse("main()\n{\n}\n", Bo3);

        Assert.DoesNotContain(tree.Root.Elements, static element => element is FunctionNode);
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3StillParsesAKeywordFunctionUnchanged()
    {
        ParseTree tree = ParserTestHelper.Parse("function main()\n{\n}\n", Bo3);

        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        Assert.Equal("main", function.NameToken.Text);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void RecoveryResyncsOnTheNextBareFunction()
    {
        // A garbled top-level line must not swallow the function after it.
        ParseTree tree = ParserTestHelper.Parse("@@@\ngood()\n{\n}\n", Cod4);

        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements.OfType<FunctionNode>()));
        Assert.Equal("good", function.NameToken.Text);
    }
}
