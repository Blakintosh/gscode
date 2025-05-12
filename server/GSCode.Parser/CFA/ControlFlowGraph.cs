using GSCode.Parser.AST;
using GSCode.Parser.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.CFA;

/// <summary>
/// Control-flow graph container, which can also serve as a subgraph.
/// </summary>
/// <param name="Start">The beginning basic block of the graph</param>
/// <param name="End">The final basic block of the graph</param>
internal readonly record struct ControlFlowGraph(CfgNode Start, CfgNode End)
{
    // Rule: the CALLER is responsible for connecting to the CALLEE's entry block, and the CALLEE is responsible for connecting to any of its successors (implied by the first rule).

    public static ControlFlowGraph ConstructFunctionGraph(FunDefnNode node, ParserIntelliSense sense)
    {
        // Function graph: (entry) -> (body) -> (exit)

        // Create the entry and exit blocks, and the function graph
        FunEntryBlock entry = new(node, node.Name);
        FunExitBlock exit = new(node);

        ControlFlowHelper functionHelper = new()
        {
            ReturnContext = exit,
            ContinuationContext = exit,
            Scope = 0
        };

        // Construct the function graph.
        LinkedListNode<AstNode>? currentNode = node.Body.Statements.First;
        CfgNode body = Construct(ref currentNode, sense, functionHelper);

        CfgNode.Connect(entry, body);

        return new(entry, exit);
    }

    private static CfgNode Construct(AstNode node, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // This is cheesy, but should work.
        LinkedListNode<AstNode>? currentNode = new LinkedListNode<AstNode>(node);

        return Construct(ref currentNode, sense, localHelper);
    }

    private static CfgNode Construct(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // If we're at the end of the current block, return the continuation context.
        if(currentNode is null)
        {
            return localHelper.ContinuationContext;
        }

        return currentNode.Value.NodeType switch
        {
            AstNodeType.IfStmt => Construct_IfStatement(ref currentNode, sense, localHelper),
            AstNodeType.WhileStmt => Construct_Skip(ref currentNode, sense, localHelper),
            AstNodeType.DoWhileStmt => Construct_Skip(ref currentNode, sense, localHelper),
            AstNodeType.ForStmt => Construct_ForStmt(ref currentNode, sense, localHelper),
            AstNodeType.ForeachStmt => Construct_ForeachStmt(ref currentNode, sense, localHelper),
            AstNodeType.SwitchStmt => Construct_Skip(ref currentNode, sense, localHelper),
            AstNodeType.BraceBlock => Construct_BraceBlock(ref currentNode, sense, localHelper),
            _ => Construct_LogicBlock(ref currentNode, sense, localHelper),
        };
    }

    private static CfgNode Construct_BraceBlock(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {

        StmtListNode stmtList = (StmtListNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        // Now, generate the brace block contents.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            ContinuationContext = continuation,
            Scope = localHelper.Scope + 1
        };

        LinkedListNode<AstNode>? blockNode = stmtList.Statements.First;
        CfgNode block = Construct(ref blockNode, sense, newLocalHelper);

        return block;
    }

    private static BasicBlock Construct_LogicBlock(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Logic block: -> (logic) -> (jump | control flow | continuation)
        LinkedList<AstNode> statements = new();

        while (currentNode != null && !IsControlFlowNode(currentNode.Value) && !IsJumpNode(currentNode.Value))
        {
            statements.AddLast(currentNode.Value);
            currentNode = currentNode.Next;
        }

        BasicBlock logic = new(statements);

        // If we reached the end of the block, just return the logic block with connection to the continuation context.
        if(currentNode is null)
        {
            CfgNode.Connect(logic, localHelper.ContinuationContext);

            return logic;
        }

        // TODO: This causes construction to stop after this jump node. While this is OK for testing purposes, it'd probably be better to construct another block of unreachable code that we can diagnose.
        if(IsJumpNode(currentNode.Value))
        {
            // TODO: verify that AST gen will ensure that the jump nodes are defined.
            // Mark the relevant jump node
            switch(currentNode.Value.NodeType)
            {
                case AstNodeType.BreakStmt:
                    CfgNode.Connect(logic, localHelper.BreakContext!);
                    break;
                case AstNodeType.ContinueStmt:
                    CfgNode.Connect(logic, localHelper.LoopContinueContext!);
                    break;
                case AstNodeType.ReturnStmt:
                    CfgNode.Connect(logic, localHelper.ReturnContext);
                    break;
            }

            statements.AddLast(currentNode.Value);
            return logic;
        }

        // We must be in control flow by this point.
        CfgNode control = Construct(ref currentNode, sense, localHelper);
        CfgNode.Connect(logic, control);

        return logic;
    }

    private static DecisionNode Construct_IfStatement(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Handle our first if
        IfStmtNode ifNode = (IfStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        DecisionNode condition = new(ifNode, ifNode.Condition);

        ControlFlowHelper ifHelper = new(localHelper)
        {
            ContinuationContext = continuation,
        };

        // Generate then.
        CfgNode then = Construct(ifNode.Then, sense, ifHelper);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If an else clause is given, construct CFG for it too.
        IfStmtNode? elseNode = ifNode.Else;
        if(elseNode is not null)
        {
            CfgNode @else = Construct_ElseIf(elseNode, sense, ifHelper);

            CfgNode.Connect(condition, @else);
            condition.WhenFalse = @else;

            return condition;
        }

        // Otherwise, connect the condition to the continuation.
        CfgNode.Connect(condition, continuation);
        condition.WhenFalse = continuation;

        return condition;
    }

    private static CfgNode Construct_ElseIf(IfStmtNode node, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Generate then.
        CfgNode then = Construct(node.Then, sense, localHelper);

        // If there's no condition, then it's the else case and we can just return the then block.
        if(node.Condition is null)
        {
            return then;
        }
        
        // Otherwise, we need to construct a decision node.
        DecisionNode condition = new(node, node.Condition);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If an else clause is given, construct CFG for it too.
        IfStmtNode? elseNode = node.Else;
        if(elseNode is not null)
        {
            CfgNode @else = Construct_ElseIf(elseNode, sense, localHelper);

            CfgNode.Connect(condition, @else);
            condition.WhenFalse = @else;

            return condition;
        }

        // Otherwise, connect the condition to the continuation.
        CfgNode.Connect(condition, localHelper.ContinuationContext);
        condition.WhenFalse = localHelper.ContinuationContext;

        return condition;
    }

    private static CfgNode Construct_ForeachStmt(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Foreach loop: (enumeration) -> (body) -> (enumeration)
        //                             -> (continuation)

        // Generate the body.
        ForeachStmtNode foreachNode = (ForeachStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        // Generate an enumeration node.
        EnumerationNode enumeration = new(foreachNode, foreachNode.KeyIdentifier, foreachNode.Collection);

        CfgNode.Connect(enumeration, continuation);
        enumeration.Continuation = continuation;

        // Now generate the body.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            LoopContinueContext = enumeration,
            BreakContext = continuation,
            ContinuationContext = continuation,
        };

        // Now generate the body of the foreach loop.
        CfgNode body = Construct(foreachNode.Then, sense, newLocalHelper);

        CfgNode.Connect(enumeration, body);
        enumeration.Body = body;

        return enumeration;
    }

    private static CfgNode Construct_ForStmt(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // For loop: (iteration) -> (body) -> (iteration)
        //                             -> (continuation)

        // Generate the body.
        ForStmtNode forNode = (ForStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        // Generate an enumeration node.
        IterationNode iteration = new(forNode, forNode.Init, forNode.Condition, forNode.Increment);

        CfgNode.Connect(iteration, continuation);
        iteration.Continuation = continuation;

        // Now generate the body.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            LoopContinueContext = iteration,
            BreakContext = continuation,
            ContinuationContext = continuation,
        };

        // Now generate the body of the foreach loop.
        CfgNode body = Construct(forNode.Then, sense, newLocalHelper);

        CfgNode.Connect(iteration, body);
        iteration.Body = body;

        return iteration;
    }

    // Temporary implementation for control flow nodes that doesn't do anything right now.
    private static CfgNode Construct_Skip(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Just get the continuation and skip the rest.
        currentNode = currentNode!.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        // Effectively: caller will connect directly to the successor.

        return continuation;
    }

    private static bool IsControlFlowNode(AstNode node)
    {
        return node.NodeType == AstNodeType.IfStmt ||
            node.NodeType == AstNodeType.WhileStmt ||
            node.NodeType == AstNodeType.DoWhileStmt ||
            node.NodeType == AstNodeType.ForStmt ||
            node.NodeType == AstNodeType.ForeachStmt ||
            node.NodeType == AstNodeType.SwitchStmt ||
            node.NodeType == AstNodeType.BraceBlock;
    }

    private static bool IsJumpNode(AstNode node)
    {
        return node.NodeType == AstNodeType.BreakStmt ||
            node.NodeType == AstNodeType.ContinueStmt ||
            node.NodeType == AstNodeType.ReturnStmt;
    }
}