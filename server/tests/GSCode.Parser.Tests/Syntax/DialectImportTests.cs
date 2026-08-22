using System.Linq;
using GSCode.Core;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// Imports fork by dialect. BO3 uses <c>#using</c> (a namespace import); the Infinity Ward games use
/// <c>#include</c> (a scope merge). Each is only valid in its own family — the directive from the
/// wrong family is reported as unknown — and BO3 is unchanged.
/// </summary>
public class DialectImportTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    [Fact]
    public void AKeywordlessDialectParsesInclude()
    {
        ParseTree tree = ParserTestHelper.Parse("#include common_scripts\\utility;\nmain()\n{\n}\n", Cod4);

        IncludeNode include = Assert.Single(tree.Root.Elements.OfType<IncludeNode>());
        Assert.Equal("common_scripts\\utility", include.Path);
        Assert.Contains(tree.Root.Elements, static element => element is FunctionNode);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3DoesNotRecognizeInclude()
    {
        // #include is not a BO3 directive, so it is reported as unknown -- unchanged behaviour.
        ParseTree tree = ParserTestHelper.Parse("#include common_scripts\\utility;\n", Bo3);

        Assert.DoesNotContain(tree.Root.Elements, static element => element is IncludeNode);
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void BlackOps3StillParsesUsing()
    {
        ParseTree tree = ParserTestHelper.Parse("#using scripts\\shared\\util_shared;\n", Bo3);

        UsingNode import = Assert.Single(tree.Root.Elements.OfType<UsingNode>());
        Assert.Equal("scripts\\shared\\util_shared", import.Path);
        Assert.Empty(tree.Diagnostics);
    }

    [Fact]
    public void AKeywordlessDialectDoesNotRecognizeUsing()
    {
        // #using is a BO3 directive; in an Infinity Ward dialect it is unknown.
        ParseTree tree = ParserTestHelper.Parse("#using scripts\\shared\\util_shared;\n", Cod4);

        Assert.DoesNotContain(tree.Root.Elements, static element => element is UsingNode);
        Assert.NotEmpty(tree.Diagnostics);
    }
}
