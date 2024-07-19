using GSCode.Parser.AST.Nodes;
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
internal readonly record struct ControlFlowGraph(BasicBlock Start, BasicBlock End)
{
    public static ControlFlowGraph Construct(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Build the maximal basic block for the logic portion
        int i = 0;
        BasicBlock logic = BuildLogicBlock(nodesStream, sense, localHelper, ref i);

        // TODO: we want unreachable code to be handled here, or in BuildLogicBlock.

        // If at the end of the block, return the logic block
        if (i == nodesStream.Length)
        {
            return new(logic, logic);
        }

        // Otherwise, construct a subgraph of the control flow we're entering and connect it.
        ControlFlowGraph control = BuildControlSubGraph(nodesStream[i..], sense, localHelper);
        logic.ConnectTo(control.Start);

        return new(logic, control.End);
    }

    public static ControlFlowGraph ConstructFunctionGraph(ASTBranch branch, ParserIntelliSense sense)
    {
        // Create the entry and exit blocks, and the function graph
        BasicBlock entry = new(0, ControlFlowType.FunctionEntry);

        Span<ASTNode> nodeStream = CollectionsMarshal.AsSpan(branch.Children);
        ControlFlowHelper functionHelper = new();

        ControlFlowGraph functionGraph = Construct(nodeStream, sense, functionHelper);

        BasicBlock exit = new(0, ControlFlowType.FunctionExit);

        // Connect the entry to the start of the function graph and the end of the function graph to the exit
        entry.ConnectTo(functionGraph.Start);
        functionGraph.End.ConnectNonJumpTo(exit);

        // Connect return edges
        functionHelper.ConnectReturnEdges(exit);

        return new(entry, exit);
    }

    private static BasicBlock BuildLogicBlock(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper, ref int i)
    {
        List<ASTNode> blockNodes = new();
        bool jumps = false;

        while (i < nodesStream.Length && !IsControlFlowNode(nodesStream[i]) && !IsJumpNode(nodesStream[i]))
        {
            blockNodes.Add(nodesStream[i]);
            i++;
        }

        // TODO: This causes construction to stop after this jump node. While this is OK for testing purposes, it'd probably be better to construct another block of unreachable code that we can diagnose.
        if(i < nodesStream.Length && IsJumpNode(nodesStream[i]))
        {
            // Mark the relevant jump node
            switch(nodesStream[i].Type)
            {
                case NodeTypes.BreakStatement:
                    localHelper.BreakBlocks.Add(new(nodesStream[i], localHelper.Scope, ControlFlowType.Logic));
                    break;
                case NodeTypes.ContinueStatement:
                    localHelper.ContinueBlocks.Add(new(nodesStream[i], localHelper.Scope, ControlFlowType.Logic));
                    break;
                case NodeTypes.ReturnStatement:
                    localHelper.ReturnBlocks.Add(new(nodesStream[i], localHelper.Scope, ControlFlowType.Logic));
                    break;
            }

            blockNodes.Add(nodesStream[i]);
            i++;
            jumps = true;
        }

        return new(blockNodes, localHelper.Scope, jumps);
    }

    private static ControlFlowGraph BuildControlSubGraph(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        ASTNode controlFlowNode = nodesStream[0];

        switch (controlFlowNode.Type)
        {
            case NodeTypes.IfStatement:
                return Construct_IfStatement(nodesStream, sense, localHelper);
            case NodeTypes.WhileLoop:
                return Construct_WhileLoop(nodesStream, sense, localHelper);
            case NodeTypes.DoLoop:
                return Construct_DoWhileLoop(nodesStream, sense, localHelper);
            case NodeTypes.ForLoop:
                return Construct_ForLoop(nodesStream, sense, localHelper);
            case NodeTypes.ForeachLoop:
                return Construct_ForeachLoop(nodesStream, sense, localHelper);
            case NodeTypes.SwitchStatement:
                return Construct_SwitchStatement(nodesStream, sense, localHelper);
            default:
                throw new NotSupportedException("Invalid control flow node type. Got "+controlFlowNode.Type);
        };
    }

    private static ControlFlowGraph ConstructFromBranchingNode(ASTNode node, ParserIntelliSense sense, ControlFlowHelper newlocalHelper)
    {
        return Construct(CollectionsMarshal.AsSpan(node.Branch!.Children), sense, newlocalHelper);
    }

    private static ControlFlowGraph Construct_IfStatement(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Handle our first if
        BasicBlock firstCondition = new(nodesStream[0], localHelper.Scope, ControlFlowType.If);

        ControlFlowGraph firstThen = ConstructFromBranchingNode(nodesStream[0], sense, localHelper.IncreaseScope());
        firstCondition.ConnectTo(firstThen.Start);

        List<ControlFlowGraph> thenGraphs = new() { firstThen };

        int i;

        // Constructs and handles the else-if conditions
        BasicBlock condition = firstCondition;
        for (i = 1; i < nodesStream.Length; i++)
        {
            ASTNode node = nodesStream[i];

            if (node.Type != NodeTypes.ElseIfStatement)
            {
                break;
            }

            // Connect the else-if condition to the previous condition
            BasicBlock elseCondition = new(node, localHelper.Scope, ControlFlowType.If);
            elseCondition.Incoming.Add(condition);

            ControlFlowGraph then = ConstructFromBranchingNode(node, sense, localHelper.IncreaseScope());
            elseCondition.ConnectTo(then.Start);

            // Now mark this as last
            condition = elseCondition;
            thenGraphs.Add(then);
        }

        bool endsWithElse = false;

        // Check for an else statement
        if (i < nodesStream.Length && nodesStream[i].Type == NodeTypes.ElseStatement)
        {
            // Construct the else body and connect it to the last condition
            ControlFlowGraph elseGraph = ConstructFromBranchingNode(nodesStream[i], sense, localHelper.IncreaseScope());
            condition.ConnectTo(elseGraph.Start);
            endsWithElse = true;

            // Add the else graph to the list
            thenGraphs.Add(elseGraph);

            i++;
        }

        // Construct the continuation block, which may be empty, then connect it to the end of all of our then blocks
        ControlFlowGraph continuationGraph = Construct(nodesStream[i..], sense, localHelper);

        // 'Backpatch' by connecting the continuation to all of the then blocks
        foreach (ControlFlowGraph then in thenGraphs)
        {
            then.End.ConnectNonJumpTo(continuationGraph.Start);
        }
        // And the continuation to the end of the final condition if it has no else
        if(!endsWithElse)
        {
            condition.ConnectTo(continuationGraph.Start);
        }

        // A graph that spans from the first if condition to the end of the continuation graph.
        return new(firstCondition, continuationGraph.End);
    }

    private static ControlFlowGraph Construct_WhileLoop(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Handle the while condition
        BasicBlock condition = new(nodesStream[0], localHelper.Scope, ControlFlowType.Loop);

        // Create loop context, process the body, and connect any continue/break statements
        ControlFlowHelper loopHelper = localHelper.EnterLoopScope();

        ControlFlowGraph body = ConstructFromBranchingNode(nodesStream[0], sense, loopHelper);
        condition.ConnectTo(body.Start);
        body.End.ConnectNonJumpTo(condition);

        // Construct the continuation block, which may be empty, then connect it to the end of the body
        ControlFlowGraph continuationGraph = Construct(nodesStream[1..], sense, localHelper);

        // Connect the continuation to the first condition
        condition.ConnectTo(continuationGraph.Start);

        // Connect loop body edges
        loopHelper.ConnectLoopEdges(condition, continuationGraph.Start);

        // A graph that spans from the while condition to the end of the continuation graph.
        return new(condition, continuationGraph.End);
    }

    private static ControlFlowGraph Construct_DoWhileLoop(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Create loop context, process the body, and connect any continue/break statements
        ControlFlowHelper loopHelper = localHelper.EnterLoopScope();

        // Create the do body
        ControlFlowGraph body = ConstructFromBranchingNode(nodesStream[0], sense, loopHelper);

        // Should probably be checked elsewhere.
        // TODO: make this better.
        if (nodesStream.Length < 2 || nodesStream[1].Type != NodeTypes.WhileLoop)
        {
            throw new InvalidOperationException("Do loop statement is missing the 'while' condition.");
        }

        // Handle the while condition
        BasicBlock condition = new(nodesStream[1], localHelper.Scope, ControlFlowType.Loop);

        body.End.ConnectNonJumpTo(condition);
        condition.ConnectTo(body.Start);

        // Construct the continuation block, which may be empty, then connect it to the end of the body
        ControlFlowGraph continuationGraph = Construct(nodesStream[1..], sense, localHelper);

        // Connect the continuation to the first condition
        condition.ConnectTo(continuationGraph.Start);

        // Connect loop body edges
        loopHelper.ConnectLoopEdges(condition, continuationGraph.Start);

        // A graph that spans from the while condition to the end of the continuation graph.
        return new(condition, continuationGraph.End);
    }

    private static ControlFlowGraph Construct_ForLoop(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // TODO: for now, we'll skip this and just build the continuation graph

        // TODO: Indexer, condition, incrementer. indexer -> condition -> body -> incrementer -|> condition
        // Handle the while condition
        //BasicBlock condition = new(nodesStream[0], baseScope, ControlFlowType.Loop);

        //ControlFlowGraph body = ConstructFromBranchingNode(nodesStream[0], sense, baseScope + 1);
        //condition.ConnectTo(body.Start);
        //body.End.ConnectTo(condition);

        // Construct the continuation block, which may be empty, then connect it to the end of the body
        ControlFlowGraph continuationGraph = Construct(nodesStream[1..], sense, localHelper);

        // Connect the continuation to the first condition
        //condition.ConnectTo(continuationGraph.Start);

        // A graph that spans from the while condition to the end of the continuation graph.
        return new(continuationGraph.Start, continuationGraph.End);
    }

    private static ControlFlowGraph Construct_ForeachLoop(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // TODO: for now, we'll skip this and just build the continuation graph

        // TODO: reduce to a for loop
        // Handle the while condition
        //BasicBlock condition = new(nodesStream[0], baseScope, ControlFlowType.Loop);

        //ControlFlowGraph body = ConstructFromBranchingNode(nodesStream[0], sense, baseScope + 1);
        //condition.ConnectTo(body.Start);
        //body.End.ConnectTo(condition);

        // Construct the continuation block, which may be empty, then connect it to the end of the body
        ControlFlowGraph continuationGraph = Construct(nodesStream[1..], sense, localHelper);

        // Connect the continuation to the first condition
        //condition.ConnectTo(continuationGraph.Start);

        // A graph that spans from the while condition to the end of the continuation graph.
        return new(continuationGraph.Start, continuationGraph.End);
    }

    private static ControlFlowGraph Construct_SwitchStatement(Span<ASTNode> nodesStream, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // TODO: for now, we'll skip this and just build the continuation graph

        // Construct the continuation block, which may be empty, then connect it to the end of the body
        ControlFlowGraph continuationGraph = Construct(nodesStream[1..], sense, localHelper);

        return new(continuationGraph.Start, continuationGraph.End);
    }

    private static bool IsControlFlowNode(ASTNode node)
    {
        return node.Type == NodeTypes.IfStatement ||
            node.Type == NodeTypes.ElseIfStatement ||
            node.Type == NodeTypes.ElseStatement ||
            node.Type == NodeTypes.WhileLoop ||
            node.Type == NodeTypes.DoLoop ||
            node.Type == NodeTypes.ForLoop ||
            node.Type == NodeTypes.ForeachLoop ||
            node.Type == NodeTypes.SwitchStatement;
    }

    private static bool IsJumpNode(ASTNode node)
    {
        return node.Type == NodeTypes.BreakStatement ||
            node.Type == NodeTypes.ContinueStatement ||
            node.Type == NodeTypes.ReturnStatement;
    }
}