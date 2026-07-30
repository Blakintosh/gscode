using GSCode.Core.Diagnostics;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

public class RecoveryTests
{
    [Fact]
    public void GarbageBetweenFunctions_BothFunctionsSurvive()
    {
        ParseTree tree = ParserTestHelper.Parse("function first()\n{\n}\n= = 12 garbage !\nfunction second()\n{\n}");

        List<FunctionNode> functions = [.. tree.Root.Elements.OfType<FunctionNode>()];
        Assert.Equal(2, functions.Count);
        Assert.Equal("first", functions[0].NameToken.Text);
        Assert.Equal("second", functions[1].NameToken.Text);
        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void BrokenStatement_NextStatementSurvives()
    {
        string printed = ParserTestHelper.PrintBody("x = ;\ny = 2;");
        Assert.Contains("(= y 2)", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSemicolon_RecoversAtNextStatement()
    {
        ParseTree tree = ParserTestHelper.Parse("function f()\n{\na = 1\nreturn;\n}");

        // 3014 rather than the generic 3000: an unterminated statement is reported at its own end
        // rather than at the token that revealed it. Recovery is unchanged — the point of this test
        // is that the next statement still parses.
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.MissingSemicolon);
        FunctionNode function = Assert.IsType<FunctionNode>(Assert.Single(tree.Root.Elements));
        Assert.Contains(function.Body.Statements, statement => statement is ReturnNode);
    }

    [Fact]
    public void UnterminatedBlock_Diagnostic()
    {
        ParseTree tree = ParserTestHelper.Parse("function f()\n{\na = 1;");
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.UnterminatedBlock);
    }

    [Fact]
    public void ClassGarbageMember_ClassAndLaterMembersSurvive()
    {
        ParseTree tree = ParserTestHelper.Parse("class C\n{\n??? nonsense\nfunction ok()\n{\n}\n}");

        ClassNode classNode = Assert.IsType<ClassNode>(Assert.Single(tree.Root.Elements));
        Assert.Contains(classNode.Members, member => member is FunctionNode);
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Code == GscDiagnosticCode.ExpectedClassMember);
    }

    [Fact]
    public void PdfFullExample_ParsesWithoutDiagnostics()
    {
        // The language reference's own end-to-end example (classes, inheritance, loops,
        // switch, deref calls), lightly normalized for its intentional typos.
        string source = """
            #using scripts\codescripts\struct;

            #namespace foo;

            #precache( "string", "TEAM_GATHER_TEAM_STEALTH_ENTER" );

            class Boo
            {
                var far;

                constructor()
                {
                    far = 1;
                }

                destructor()
                {
                }

                function faz( value = 0 )
                {
                    far = value;
                }
            }

            class Faz : Boo
            {
                var far2;

                constructor()
                {
                    far2 = 2;
                }

                function faz( value1 = 1, value2 = 2 )
                {
                    Boo::faz(value1);
                    far2 = value2;
                }
            }

            function flop()
            {
                boo_object = new Boo();
                faz_object = new Faz();
                [[boo_object]]->faz();
                [[faz_object]]->faz(undefined, 1);

                a = [];
                i = 0;
                do
                {
                    a[i] = i;
                    i++;
                }
                while ( i < 10 );

                a = [];
                for ( i = 0; i < 10; i++ )
                {
                    a[i] = i;
                }

                foreach ( key, value in a )
                {
                    println( "key is " + key + " and value is " + value + "\n" );
                }

                v = 1;
                v2 = "default";

                switch ( v )
                {
                    case 0:
                        v2 = "0";
                        break;
                    case 1:
                        v2 = "1";
                        break;
                    default:
                        v2 = "default";
                        break;
                }
            }
            """;

        ParseTree tree = ParserTestHelper.Parse(source);
        Assert.Empty(tree.Diagnostics);

        // using + namespace + precache + Boo + Faz + flop.
        Assert.Equal(6, tree.Root.Elements.Length);
    }
}
