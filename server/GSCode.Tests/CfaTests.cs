using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;

namespace GSCode.Tests;

public class CfaTests
{
    [Fact]
    public void Test_BasicFunction()
    {
        // Expectation:
        // (entry) -> (logic) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null),
                new ExprStmtNode(null),
                new ExprStmtNode(null),
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root, 
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode outgoing = Assert.Single(cfg.Start.Outgoing);
        CfgNode incoming = Assert.Single(cfg.End.Incoming);

        Assert.IsType<BasicBlock>(outgoing);
        Assert.Equal(outgoing, incoming);
    }

    [Fact]
    public void Test_SimpleIf()
    {
        // Expectation:
        // (entry) -> (logic) -> (if)   -> (then) -> (logic_cont) -> (exit)
        //                              --------------^

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null),
                new IfStmtNode(DataExprNode.From(new Token(TokenType.True, RangeHelper.From(0, 0, 0, 1), "true")))
                {
                    Then = new ExprStmtNode(null),
                    Else = null,
                },
                new ExprStmtNode(null),
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root, 
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));
            
        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode outgoing = Assert.Single(cfg.Start.Outgoing);

        Assert.IsType<BasicBlock>(outgoing);
        CfgNode ifBlock = Assert.Single(outgoing.Outgoing);
        DecisionNode decision = Assert.IsType<DecisionNode>(ifBlock);

        Assert.IsType<BasicBlock>(decision.WhenTrue);
        Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode outgoingTrue = Assert.Single(decision.WhenTrue.Outgoing);
        Assert.Equal(outgoingTrue, decision.WhenFalse);
    }

    [Fact]
    public void Test_SimpleIfElse()
    {
        // Expectation:
        // (entry) -> (logic) -> (if) --(true)--> (then) --\
        //                              --(false)-> (else) --+--> (logic_cont) -> (exit)

        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                new ExprStmtNode(null), // logic
                new IfStmtNode(DataExprNode.From(new Token(TokenType.True, RangeHelper.From(0, 0, 0, 1), "true")))
                {
                    Then = new ExprStmtNode(null), // then
                    Else = new IfStmtNode(null)
                    {
                        Then = new ExprStmtNode(null), // then
                        Else = null, // else
                    },
                },
                new ExprStmtNode(null), // logic_cont
            ])
        };

        ControlFlowGraph cfg = ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));

        Assert.NotNull(cfg.Start);
        Assert.NotNull(cfg.End);

        CfgNode preIfBlock = Assert.Single(cfg.Start.Outgoing); // logic
        Assert.IsType<BasicBlock>(preIfBlock);

        CfgNode ifBlock = Assert.Single(preIfBlock.Outgoing); // if
        DecisionNode decision = Assert.IsType<DecisionNode>(ifBlock);

        Assert.NotNull(decision.WhenTrue); // then
        BasicBlock thenBlock = Assert.IsType<BasicBlock>(decision.WhenTrue);

        Assert.NotNull(decision.WhenFalse); // else
        BasicBlock elseBlock = Assert.IsType<BasicBlock>(decision.WhenFalse);

        CfgNode afterThen = Assert.Single(thenBlock.Outgoing); // logic_cont
        CfgNode afterElse = Assert.Single(elseBlock.Outgoing); // logic_cont

        Assert.Equal(afterThen, afterElse); // Common continuation point
        Assert.IsType<BasicBlock>(afterThen);

        CfgNode exitNode = Assert.Single(afterThen.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    private StmtListNode StmtListNodeFromList(List<AstNode> statements)
    {
        LinkedList<AstNode> list = new();
        foreach(AstNode statement in statements)
        {
            list.AddLast(statement);
        }

        return new StmtListNode(list);
    }
}
