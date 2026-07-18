using GSCode.Core.Diagnostics;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

public class DeclarationTests
{
    [Fact]
    public void Using_ParsesPath()
    {
        string printed = ParserTestHelper.PrintScript(@"#using scripts\shared\util_shared;");
        Assert.Equal(@"(script (using ""scripts\shared\util_shared""))", printed);
    }

    [Fact]
    public void Using_AfterFunction_Diagnostic()
    {
        ParseTree tree = ParserTestHelper.Parse("function f()\n{\n}\n#using scripts\\foo;");
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.UsingAfterDeclaration);
    }

    [Fact]
    public void Namespace_Parses()
    {
        Assert.Equal("(script (namespace sound))", ParserTestHelper.PrintScript("#namespace sound;"));
    }

    [Fact]
    public void Precache_KeepsRawArguments()
    {
        string printed = ParserTestHelper.PrintScript("#precache(\"string\", \"HINT_TEXT\");");
        Assert.Equal("(script (precache \"string\" \"HINT_TEXT\"))", printed);
    }

    [Fact]
    public void UsingAnimTree_Parses()
    {
        Assert.Equal("(script (using_animtree \"generic\"))", ParserTestHelper.PrintScript("#using_animtree(\"generic\");"));
    }

    [Fact]
    public void Function_Simple()
    {
        string printed = ParserTestHelper.PrintScript("function foo()\n{\n}");
        Assert.Equal("(script (function foo (params) (block)))", printed);
    }

    [Fact]
    public void Function_ModifiersParametersDefaultsByRefVarargs()
    {
        string printed = ParserTestHelper.PrintScript("function private autoexec foo( a, b = 5, &c, ... )\n{\n}");
        Assert.Equal("(script (function private autoexec foo (params (a) (b = 5) (&c) ...) (block)))", printed);
    }

    [Fact]
    public void Class_WithInheritanceMembersAndMethods()
    {
        string source = """
            class Faz : Boo
            {
                var far2;

                constructor()
                {
                    far2 = 2;
                }

                destructor()
                {
                }

                function faz( value1 = 1 )
                {
                    far2 = value1;
                }
            }
            """;

        string printed = ParserTestHelper.PrintScript(source);
        Assert.Equal(
            "(script (class Faz : Boo (var far2) (constructor (block (= far2 2))) (destructor (block)) (function faz (params (value1 = 1)) (block (= far2 value1)))))",
            printed);
    }

    [Fact]
    public void DevBlock_AtTopLevel_WrapsDeclarations()
    {
        string printed = ParserTestHelper.PrintScript("/#\nfunction debug_thing()\n{\n}\n#/");
        Assert.Equal("(script (devblock (function debug_thing (params) (block))))", printed);
    }

    [Fact]
    public void MultipleNamespaces_StayInOrder()
    {
        string source = "#namespace sound;\nfunction foo()\n{\n}\n#namespace audio;\nfunction bar()\n{\n}";
        string printed = ParserTestHelper.PrintScript(source);
        Assert.Equal(
            "(script (namespace sound) (function foo (params) (block)) (namespace audio) (function bar (params) (block)))",
            printed);
    }

    [Fact]
    public void PdfExample_SoundShared_Parses()
    {
        string source = """
            #using scripts\shared\util_shared;

            #namespace sound;
            function foo( alias, origin = (0,0,0) , ender )
            {
            }
            """;

        ParseTree tree = ParserTestHelper.Parse(source);
        Assert.Empty(tree.Diagnostics);

        FunctionNode function = Assert.IsType<FunctionNode>(tree.Root.Elements[^1]);
        Assert.Equal(3, function.Parameters.Length);
        Assert.IsType<VectorNode>(function.Parameters[1].DefaultValue);
    }
}
