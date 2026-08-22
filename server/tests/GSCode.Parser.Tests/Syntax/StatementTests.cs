using Xunit;

namespace GSCode.Parser.Tests.Syntax;

public class StatementTests
{
    [Fact]
    public void IfElse_Parses()
    {
        string printed = ParserTestHelper.PrintBody("if ( a )\n{\nb = 1;\n}\nelse\n{\nb = 2;\n}");
        Assert.Equal("(block (if a (block (= b 1)) else (block (= b 2))))", printed);
    }

    [Fact]
    public void If_SingleStatementBody_NoBraces()
    {
        string printed = ParserTestHelper.PrintBody("if ( a )\nb = 1;");
        Assert.Equal("(block (if a (= b 1)))", printed);
    }

    [Fact]
    public void WhileLoop_Parses()
    {
        Assert.Equal("(block (while (< i 10) (block (postfix++ i))))", ParserTestHelper.PrintBody("while ( i < 10 )\n{\ni++;\n}"));
    }

    [Fact]
    public void DoWhile_Parses()
    {
        Assert.Equal("(block (do (block (postfix++ i)) while (< i 10)))", ParserTestHelper.PrintBody("do\n{\ni++;\n}\nwhile ( i < 10 );"));
    }

    [Fact]
    public void For_AllParts()
    {
        Assert.Equal(
            "(block (for (= i 0) (< i 10) (postfix++ i) (block (= a i))))",
            ParserTestHelper.PrintBody("for ( i = 0; i < 10; i++ )\n{\na = i;\n}"));
    }

    [Fact]
    public void For_EmptyParts_InfiniteLoop()
    {
        Assert.Equal("(block (for _ _ _ (block (break))))", ParserTestHelper.PrintBody("for ( ; ; )\n{\nbreak;\n}"));
    }

    [Fact]
    public void Foreach_KeyValue()
    {
        Assert.Equal(
            "(block (foreach key value in a (block (call print key))))",
            ParserTestHelper.PrintBody("foreach ( key, value in a )\n{\nprint(key);\n}"));
    }

    [Fact]
    public void Foreach_ValueOnly()
    {
        Assert.Equal(
            "(block (foreach player in (. level players) (block)))",
            ParserTestHelper.PrintBody("foreach ( player in level.players )\n{\n}"));
    }

    [Fact]
    public void Switch_CasesDefaultAndStackedLabels()
    {
        string source = """
            switch ( v )
            {
                case 0:
                    a = 0;
                    break;
                case 1:
                case 2:
                    a = 12;
                    break;
                default:
                    a = 9;
                    break;
            }
            """;

        Assert.Equal(
            "(block (switch v (case 0 (= a 0) (break)) (case 1 2 (= a 12) (break)) (case default (= a 9) (break))))",
            ParserTestHelper.PrintBody(source));
    }

    [Fact]
    public void Return_WithAndWithoutValue()
    {
        Assert.Equal("(block (return a))", ParserTestHelper.PrintBody("return a;"));
        Assert.Equal("(block (return))", ParserTestHelper.PrintBody("return;"));
    }

    [Fact]
    public void WaitForms_Parse()
    {
        Assert.Equal("(block (wait 0.05))", ParserTestHelper.PrintBody("wait 0.05;"));
        Assert.Equal("(block (wait (paren 1)))", ParserTestHelper.PrintBody("wait(1);"));
        Assert.Equal("(block (waitrealtime 2))", ParserTestHelper.PrintBody("waitrealtime 2;"));
        Assert.Equal("(block (waittillframeend))", ParserTestHelper.PrintBody("waittillframeend;"));
    }

    [Fact]
    public void ConstDeclaration_Parses()
    {
        Assert.Equal("(block (const MAX = 10))", ParserTestHelper.PrintBody("const MAX = 10;"));
    }

    [Fact]
    public void DevBlock_AtStatementLevel()
    {
        Assert.Equal(
            "(block (devblock (call println \"debug\")))",
            ParserTestHelper.PrintBody("/#\nprintln(\"debug\");\n#/"));
    }

    [Fact]
    public void EmptyStatement_Ignored()
    {
        Assert.Equal("(block (empty) (= a 1))", ParserTestHelper.PrintBody(";\na = 1;"));
    }
}
