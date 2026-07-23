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
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;
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
    public void Mw2ParsesAFileScopeConstant()
    {
        ParseTree tree = ParserTestHelper.Parse("MAX = 130;\nrun()\n{\n}\n", Mw2);

        FileScopeConstantNode constant = Assert.Single(tree.Root.Elements.OfType<FileScopeConstantNode>());
        Assert.Equal("MAX", constant.NameToken.Text);
        Assert.Contains(tree.Root.Elements, static element => element is FunctionNode);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void FileScopeConstantsCanReferenceEachOther()
    {
        ParseTree tree = ParserTestHelper.Parse("A = 1;\nB = A + 1;\n", Mw2);

        Assert.Equal(2, tree.Root.Elements.OfType<FileScopeConstantNode>().Count());
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3RejectsAFileScopeConstant()
    {
        // BO3 uses #define; a bare top-level assignment is not a declaration.
        ParseTree tree = ParserTestHelper.Parse("MAX = 130;\n", Bo3);

        Assert.DoesNotContain(tree.Root.Elements, static element => element is FileScopeConstantNode);
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void Cod4HasNoFileScopeConstants()
    {
        // The axis is MW2-onward; CoD4 does not have them.
        ParseTree tree = ParserTestHelper.Parse("MAX = 130;\n", Cod4);

        Assert.DoesNotContain(tree.Root.Elements, static element => element is FileScopeConstantNode);
        Assert.NotEmpty(tree.Diagnostics);
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
