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

    [Fact]
    public void Test_SimpleSwitch()
    {
        // Expectation:
        // (entry) -> (switch) -> (case1Label) -> (case2Label) -> (continuation/exit)
        //                        case1Label (whenTrue) -> body1 -> (continuation)
        //                        case2Label (whenTrue) -> body2 -> (continuation)

        CaseStmtNode case1 = CreateCase(1, [new ExprStmtNode(null), BreakStmt()]);
        CaseStmtNode case2 = CreateCase(2, [new ExprStmtNode(null), BreakStmt()]);

        SwitchStmtNode switchStmt = CreateSwitch([case1, case2]);

        ControlFlowGraph cfg = BuildSwitchCfg(switchStmt);

        // entry -> switch node
        SwitchNode switchNode = GetSingleOutgoing<SwitchNode>(cfg.Start);

        // switch -> case1Label
        SwitchDecisionNode case1Decision = GetSingleOutgoing<SwitchDecisionNode>(switchNode);

        // case1Label -> case2Label (when false)
        SwitchDecisionNode case2Decision = Assert.IsType<SwitchDecisionNode>(case1Decision.WhenFalse);

        // case1Label -> body1 (when true)
        BasicBlock body1 = Assert.IsType<BasicBlock>(case1Decision.WhenTrue);

        // case2Label -> body2 (when true)
        BasicBlock body2 = Assert.IsType<BasicBlock>(case2Decision.WhenTrue);

        // Both bodies should have break statements, so they connect to continuation
        CfgNode contFromBody1 = Assert.Single(body1.Outgoing);
        CfgNode contFromBody2 = Assert.Single(body2.Outgoing);

        // Both should connect to the same continuation point
        Assert.Equal(contFromBody1, contFromBody2);

        // case2Label's WhenFalse should also point to continuation (last case, no default)
        Assert.Equal(case2Decision.WhenFalse, contFromBody1);

        // Continuation should be a basic block (the statement after switch)
        BasicBlock continuation = Assert.IsType<BasicBlock>(contFromBody1);

        // Continuation should connect to exit
        CfgNode exitNode = Assert.Single(continuation.Outgoing);
        Assert.Equal(cfg.End, exitNode);
    }

    private StmtListNode StmtListNodeFromList(List<AstNode> statements)
    {
        LinkedList<AstNode> list = new();
        foreach (AstNode statement in statements)
        {
            list.AddLast(statement);
        }

        return new StmtListNode(list);
    }

    // Switch test helpers

    /// <summary>
    /// Creates a case statement with a single case label and the given body statements.
    /// </summary>
    private CaseStmtNode CreateCase(int labelValue, List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        caseStmt.Labels.AddLast(CreateCaseLabel(labelValue));
        return caseStmt;
    }

    /// <summary>
    /// Creates a default case statement with the given body statements.
    /// </summary>
    private CaseStmtNode CreateDefaultCase(List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        caseStmt.Labels.AddLast(CreateDefaultLabel());
        return caseStmt;
    }

    /// <summary>
    /// Creates a case statement with multiple labels and the given body statements.
    /// </summary>
    private CaseStmtNode CreateCaseWithMultipleLabels(List<int> labelValues, List<AstNode> bodyStatements)
    {
        CaseStmtNode caseStmt = new()
        {
            Body = StmtListNodeFromList(bodyStatements)
        };
        foreach (int value in labelValues)
        {
            caseStmt.Labels.AddLast(CreateCaseLabel(value));
        }
        return caseStmt;
    }

    /// <summary>
    /// Creates a case label node with an integer value.
    /// </summary>
    private CaseLabelNode CreateCaseLabel(int value)
    {
        return new CaseLabelNode(
            AstNodeType.CaseLabel,
            new Token(TokenType.Case, RangeHelper.From(0, 0, 0, 4), "case"),
            DataExprNode.From(new Token(TokenType.Integer, RangeHelper.From(0, 0, 0, 1), value.ToString()))
        );
    }

    /// <summary>
    /// Creates a default label node.
    /// </summary>
    private CaseLabelNode CreateDefaultLabel()
    {
        return new CaseLabelNode(
            AstNodeType.DefaultLabel,
            new Token(TokenType.Default, RangeHelper.From(0, 0, 0, 7), "default"),
            null
        );
    }

    /// <summary>
    /// Creates a switch statement from a list of cases.
    /// </summary>
    private SwitchStmtNode CreateSwitch(List<CaseStmtNode> cases, ExprNode? expression = null)
    {
        CaseListNode caseList = new();
        foreach (CaseStmtNode caseStmt in cases)
        {
            caseList.Cases.AddLast(caseStmt);
        }

        return new SwitchStmtNode()
        {
            Expression = expression ?? DataExprNode.From(new Token(TokenType.Integer, RangeHelper.From(0, 0, 0, 1), "1")),
            Cases = caseList
        };
    }

    /// <summary>
    /// Builds a CFG for a function containing a switch statement and a continuation statement.
    /// </summary>
    private ControlFlowGraph BuildSwitchCfg(SwitchStmtNode switchStmt)
    {
        FunDefnNode root = new()
        {
            Name = null,
            Keywords = new(),
            Parameters = new(),
            Body = StmtListNodeFromList([
                switchStmt,
                new ExprStmtNode(null), // continuation after switch
            ])
        };

        return ControlFlowGraph.ConstructFunctionGraph(
            root,
            new ParserIntelliSense(0, new DocumentUri("", "", "", "", ""), ""));
    }

    /// <summary>
    /// Creates a break statement node.
    /// </summary>
    private ControlFlowActionNode BreakStmt()
    {
        return new ControlFlowActionNode(
            AstNodeType.BreakStmt,
            new Token(TokenType.Break, RangeHelper.From(0, 0, 0, 5), "break")
        );
    }

    /// <summary>
    /// Creates a continue statement node.
    /// </summary>
    private ControlFlowActionNode ContinueStmt()
    {
        return new ControlFlowActionNode(
            AstNodeType.ContinueStmt,
            new Token(TokenType.Continue, RangeHelper.From(0, 0, 0, 8), "continue")
        );
    }

    /// <summary>
    /// Gets the single outgoing node and asserts it's of the expected type.
    /// </summary>
    private T GetSingleOutgoing<T>(CfgNode node) where T : CfgNode
    {
        CfgNode outgoing = Assert.Single(node.Outgoing);
        return Assert.IsType<T>(outgoing);
    }
}
