using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using Xunit;

namespace GSCode.Parser.Tests.Syntax;

/// <summary>
/// What <see cref="AstSearch.ChildrenOf"/> yields, and in what order, for each shape in the tree.
///
/// Every AST walk in the server goes through it — fifteen lints, the reference index, the inlay
/// hints, the parameter typer — so a child that is skipped or reordered is a wrong answer in all of
/// them at once, and silently: a rule that never sees a node simply reports nothing about it. These
/// pin the order per shape, and the last one pins that the walk allocates nothing, which is the
/// reason the enumerable is a struct.
/// </summary>
public class ChildEnumerationTests
{
    /// <summary>The child type names of one node, in order.</summary>
    private static string ChildrenOf(AstNode node)
    {
        List<string> names = [];
        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            names.Add(child.GetType().Name);
        }

        return string.Join(" ", names);
    }

    private static AstNode FirstElement(string source)
    {
        return Assert.Single(ParserTestHelper.Parse(source).Root.Elements);
    }

    /// <summary>The first statement of "function test() { ... }".</summary>
    private static AstNode FirstStatement(string statements)
    {
        FunctionNode function = Assert.IsType<FunctionNode>(FirstElement("function test()\n{\n" + statements + "\n}"));
        BlockNode body = Assert.IsType<BlockNode>(function.Body);
        return body.Statements[0];
    }

    [Fact]
    public void AFunctionYieldsItsParametersThenItsBody()
    {
        AstNode function = FirstElement("function test( a, b = 1 )\n{\n}");

        Assert.Equal("ParameterNode ParameterNode BlockNode", ChildrenOf(function));
    }

    [Fact]
    public void AParameterYieldsItsDefaultOnlyWhenItHasOne()
    {
        FunctionNode function = Assert.IsType<FunctionNode>(FirstElement("function test( a, b = 1 )\n{\n}"));

        Assert.Equal("", ChildrenOf(function.Parameters[0]));
        Assert.Equal("LiteralNode", ChildrenOf(function.Parameters[1]));
    }

    [Fact]
    public void AnIfYieldsConditionThenBranchAndTheElseOnlyWhenPresent()
    {
        Assert.Equal("IdentifierNode BlockNode", ChildrenOf(FirstStatement("if ( a )\n{\n}")));
        Assert.Equal("IdentifierNode BlockNode BlockNode", ChildrenOf(FirstStatement("if ( a )\n{\n}\nelse\n{\n}")));
    }

    [Fact]
    public void AForYieldsOnlyTheClausesItHas()
    {
        Assert.Equal(
            "ExprStatementNode BinaryNode ExprStatementNode BlockNode",
            ChildrenOf(FirstStatement("for ( i = 0; i < 4; i++ )\n{\n}")));

        // A bare `for ( ;; )` keeps its body and nothing else, so the body must not be mistaken for
        // an initializer by a walk that counts positions rather than reading them.
        Assert.Equal("BlockNode", ChildrenOf(FirstStatement("for ( ;; )\n{\n}")));
    }

    [Fact]
    public void ADoWhileYieldsItsBodyBeforeItsCondition()
    {
        Assert.Equal("BlockNode IdentifierNode", ChildrenOf(FirstStatement("do\n{\n}\nwhile ( a );")));
    }

    [Fact]
    public void ASwitchYieldsItsSubjectThenItsCaseGroups()
    {
        Assert.Equal(
            "IdentifierNode CaseGroupNode CaseGroupNode",
            ChildrenOf(FirstStatement("switch ( a )\n{\ncase 1:\n    break;\ndefault:\n    break;\n}")));
    }

    [Fact]
    public void ACaseGroupYieldsItsLabelValuesThenItsStatements()
    {
        SwitchNode switchNode = Assert.IsType<SwitchNode>(
            FirstStatement("switch ( a )\n{\ncase 1:\ncase 2:\n    b = 1;\n    break;\n}"));

        Assert.Equal("LiteralNode LiteralNode ExprStatementNode BreakNode", ChildrenOf(switchNode.Cases[0]));
    }

    [Fact]
    public void ADefaultLabelContributesNoChild()
    {
        // `default:` is a label with no value. It must be skipped rather than yielded as null, and
        // rather than stopping the labels that follow it in a group.
        SwitchNode switchNode = Assert.IsType<SwitchNode>(
            FirstStatement("switch ( a )\n{\ndefault:\ncase 1:\n    break;\n}"));

        Assert.Equal("LiteralNode BreakNode", ChildrenOf(switchNode.Cases[0]));
    }

    [Fact]
    public void ACallYieldsItsTargetThenItsCalleeThenItsArguments()
    {
        ExprStatementNode statement = Assert.IsType<ExprStatementNode>(FirstStatement("player thread f( 1, 2 );"));
        CallNode call = Assert.IsType<CallNode>(statement.Expression);

        Assert.Equal("IdentifierNode IdentifierNode LiteralNode LiteralNode", ChildrenOf(call));
    }

    [Fact]
    public void ACallWithNoTargetYieldsItsCalleeFirst()
    {
        ExprStatementNode statement = Assert.IsType<ExprStatementNode>(FirstStatement("f( 1 );"));
        CallNode call = Assert.IsType<CallNode>(statement.Expression);

        Assert.Equal("IdentifierNode LiteralNode", ChildrenOf(call));
    }

    [Fact]
    public void AVectorYieldsThreeComponentsAndATernaryThreeOperands()
    {
        ExprStatementNode vectorStatement = Assert.IsType<ExprStatementNode>(FirstStatement("a = ( 1, 2, 3 );"));
        AssignmentNode vectorAssignment = Assert.IsType<AssignmentNode>(vectorStatement.Expression);
        Assert.Equal("LiteralNode LiteralNode LiteralNode", ChildrenOf(vectorAssignment.Value));

        ExprStatementNode ternaryStatement = Assert.IsType<ExprStatementNode>(FirstStatement("a = b ? 1 : 2;"));
        AssignmentNode ternaryAssignment = Assert.IsType<AssignmentNode>(ternaryStatement.Expression);
        Assert.Equal("IdentifierNode LiteralNode LiteralNode", ChildrenOf(ternaryAssignment.Value));
    }

    [Fact]
    public void ALeafYieldsNothing()
    {
        ExprStatementNode statement = Assert.IsType<ExprStatementNode>(FirstStatement("a;"));

        Assert.Equal("", ChildrenOf(statement.Expression));
    }

    [Fact]
    public void AReturnYieldsItsValueOnlyWhenItHasOne()
    {
        Assert.Equal("", ChildrenOf(FirstStatement("return;")));
        Assert.Equal("LiteralNode", ChildrenOf(FirstStatement("return 1;")));
    }

    [Fact]
    public void WalkingTheWholeTreeAllocatesNothing()
    {
        // The point of the struct enumerable. The iterator this replaced allocated one state machine
        // per node VISITED, which a walk repeated by every rule pays for again each time.
        ScriptNode root = ParserTestHelper.Parse(
            """
            #using scripts\shared\util_shared;

            function test( a, b = 1 )
            {
                for ( i = 0; i < 4; i++ )
                {
                    if ( a[ i ] > 2 )
                    {
                        a thread f( i, ( 1, 2, 3 ) );
                    }
                    else
                    {
                        switch ( b )
                        {
                            case 1:
                            default:
                                b = b ? 1 : 2;
                                break;
                        }
                    }
                }

                return b;
            }
            """).Root;

        // Warmed first: the assertion is about the walk, not about the JIT reaching it.
        int warm = Count(root);
        Assert.True(warm > 30, $"expected a tree worth walking, counted {warm}");

        long before = GC.GetAllocatedBytesForCurrentThread();
        int counted = Count(root);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(warm, counted);
        Assert.Equal(0, after - before);
    }

    private static int Count(AstNode node)
    {
        int count = 1;
        foreach ( AstNode child in AstSearch.ChildrenOf(node) )
        {
            count += Count(child);
        }

        return count;
    }
}
