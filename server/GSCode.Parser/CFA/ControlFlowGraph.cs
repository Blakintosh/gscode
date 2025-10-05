using GSCode.Data;
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

    private static CfgNode Construct(AstNode node, ParserIntelliSense sense, ControlFlowHelper localHelper, bool shouldIncreaseScope = true)
    {
        // This is cheesy, but should work.
        LinkedListNode<AstNode>? currentNode = new LinkedListNode<AstNode>(node);

        return Construct(ref currentNode, sense, localHelper, shouldIncreaseScope);
    }

    private static CfgNode Construct(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper, bool shouldIncreaseScope = true)
    {
        // If we're at the end of the current block, return the continuation context.
        if (currentNode is null)
        {
            return localHelper.ContinuationContext;
        }

        return currentNode.Value.NodeType switch
        {
            AstNodeType.IfStmt => Construct_IfStatement(ref currentNode, sense, localHelper),
            AstNodeType.WhileStmt => Construct_WhileStatement(ref currentNode, sense, localHelper),
            AstNodeType.DoWhileStmt => Construct_DoWhileStatement(ref currentNode, sense, localHelper),
            AstNodeType.ForStmt => Construct_ForStmt(ref currentNode, sense, localHelper),
            AstNodeType.ForeachStmt => Construct_ForeachStmt(ref currentNode, sense, localHelper),
            AstNodeType.SwitchStmt => Construct_SwitchStmt(ref currentNode, sense, localHelper),
            AstNodeType.BraceBlock => Construct_BraceBlock(ref currentNode, sense, localHelper, shouldIncreaseScope),
            _ => Construct_LogicBlock(ref currentNode, sense, localHelper),
        };
    }

    private static CfgNode Construct_BraceBlock(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper, bool shouldIncreaseScope)
    {

        StmtListNode stmtList = (StmtListNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        // Now, generate the brace block contents.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            ContinuationContext = continuation,
            // TODO: GSC does NOT use lexical scope within functions, but we need to consider class scope (as class members can only be used by methods that occur after their definition). 
            // Leave this commented for now.
            // Scope = shouldIncreaseScope ? localHelper.Scope + 1 : localHelper.Scope
            Scope = localHelper.Scope
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

        BasicBlock logic = new(statements, localHelper.Scope);

        // If we reached the end of the block, just return the logic block with connection to the continuation context.
        if (currentNode is null)
        {
            CfgNode.Connect(logic, localHelper.ContinuationContext);

            return logic;
        }

        // TODO: This causes construction to stop after this jump node. While this is OK for testing purposes, it'd probably be better to construct another block of unreachable code that we can diagnose.
        if (IsJumpNode(currentNode.Value))
        {
            // TODO: verify that AST gen will ensure that the jump nodes are defined.
            // Mark the relevant jump node
            switch (currentNode.Value.NodeType)
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

        DecisionNode condition = new(ifNode, ifNode.Condition, localHelper.Scope);

        ControlFlowHelper ifHelper = new(localHelper)
        {
            ContinuationContext = continuation,
        };

        // Generate then.
        CfgNode then = Construct(ifNode.Then, sense, ifHelper, false);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If an else clause is given, construct CFG for it too.
        IfStmtNode? elseNode = ifNode.Else;
        if (elseNode is not null)
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
        CfgNode then = Construct(node.Then, sense, localHelper, false);

        // If there's no condition, then it's the else case and we can just return the then block.
        if (node.Condition is null)
        {
            return then;
        }

        // Otherwise, we need to construct a decision node.
        DecisionNode condition = new(node, node.Condition, localHelper.Scope);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If an else clause is given, construct CFG for it too.
        IfStmtNode? elseNode = node.Else;
        if (elseNode is not null)
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

    private static DecisionNode Construct_WhileStatement(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Handle the while statement
        WhileStmtNode whileNode = (WhileStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        DecisionNode condition = new(whileNode, whileNode.Condition, localHelper.Scope);

        ControlFlowHelper whileHelper = new(localHelper)
        {
            LoopContinueContext = condition,
            ContinuationContext = condition,
            BreakContext = continuation,
        };

        // Generate the body of the while loop.
        CfgNode then = Construct(whileNode.Then, sense, whileHelper, false);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If false, then use the continuation.
        CfgNode.Connect(condition, continuation);
        condition.WhenFalse = continuation;

        return condition;
    }

    private static CfgNode Construct_DoWhileStatement(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        // Handle the do-while statement
        DoWhileStmtNode doWhileNode = (DoWhileStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        DecisionNode condition = new(doWhileNode, doWhileNode.Condition, localHelper.Scope);

        ControlFlowHelper whileHelper = new(localHelper)
        {
            LoopContinueContext = condition,
            ContinuationContext = condition,
            BreakContext = continuation,
        };

        // Generate the body of the do-while loop.
        CfgNode then = Construct(doWhileNode.Then, sense, whileHelper, false);

        CfgNode.Connect(condition, then);
        condition.WhenTrue = then;

        // If false, then use the continuation.
        CfgNode.Connect(condition, continuation);
        condition.WhenFalse = continuation;

        // Unlike while, the body is hit first.
        return then;
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
        EnumerationNode enumeration = new(foreachNode, localHelper.Scope /*+ 1*/);

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
        IterationNode iteration = new(forNode, forNode.Init, forNode.Condition, forNode.Increment, localHelper.Scope);

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

    private static CfgNode Construct_SwitchStmt(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense, ControlFlowHelper localHelper)
    {
        SwitchStmtNode switchAstNode = (SwitchStmtNode)currentNode!.Value;

        // Get the continuation first.
        currentNode = currentNode.Next;
        CfgNode continuation = Construct(ref currentNode, sense, localHelper);

        SwitchNode switchNode = new(switchAstNode, continuation, localHelper.Scope);

        // If there are no cases to analyse, then just early return.
        if (switchAstNode.Cases.Cases.Count == 0)
        {
            return switchNode;
        }

        SwitchHelper switchHelper = new(continuation);

        // Build the labels recursively, which in practice will go backwards, then we'll connect to the first label.
        CfgNode firstLabel = Construct_SwitchBranch(switchAstNode.Cases.Cases.First!, sense, localHelper, switchHelper, out _);

        CfgNode.Connect(switchNode, firstLabel);
        switchNode.FirstLabel = firstLabel;

        return switchNode;
    }

    private static CfgNode Construct_SwitchBranch(LinkedListNode<CaseStmtNode> @case, ParserIntelliSense sense, ControlFlowHelper localHelper, SwitchHelper switchHelper, out CfgNode branchBody)
    {
        CaseStmtNode current = @case.Value;

        // Step 1: proceed forward and construct all labels.
        CfgNode? firstLabel = null;
        SwitchDecisionNode? previousLabel = null;

        List<SwitchDecisionNode> labels = new();

        for (LinkedListNode<CaseLabelNode>? node = @case.Value.Labels.First; node != null; node = node.Next)
        {
            CaseLabelNode label = node.Value;

            // Create a decision node for this label.
            SwitchDecisionNode decision = new(label, localHelper.Scope);

            // If this label is the default, use it as the unmatched node but don't connect it in this context.
            if (label.NodeType == AstNodeType.DefaultLabel)
            {
                // If it's a duplicate default label, emit an error and skip it.
                if (switchHelper.ContainsDefaultLabel)
                {
                    sense.AddSpaDiagnostic(label.Value.Range, GSCErrorCodes.MultipleDefaultLabels);
                    continue;
                }
                switchHelper.UnmatchedNode = decision;
                switchHelper.ContainsDefaultLabel = true;
                labels.Add(decision);
                continue;
            }

            firstLabel ??= decision;

            // Connect the previous label to this one, if it exists.
            if (previousLabel is not null)
            {
                CfgNode.Connect(previousLabel, decision);
                previousLabel.WhenFalse = decision;
            }

            previousLabel = decision;
            labels.Add(decision);
        }

        // Track what the continuation for the body should be, ie in case of fall-through.
        CfgNode continuationForBody = switchHelper.Continuation;

        // Step 2: recurse into other branches, and let them generate their labels, and bodies backwards.
        if (@case.Next is not null)
        {
            CfgNode nextBranchLabel = Construct_SwitchBranch(@case.Next!, sense, localHelper, switchHelper, out continuationForBody);

            // Connect our previous (ie last) label to the next branch's first label.
            if (previousLabel is not null)
            {
                CfgNode.Connect(previousLabel!, nextBranchLabel);
                previousLabel!.WhenFalse = nextBranchLabel;
            }
            else
            {
                // If previous label is null, then so is first label, which means this is a default-only case. We'll have to make the caller jump over us, then.
                firstLabel = nextBranchLabel;
            }
        }
        // Or if there's no next branch, then we're at the final label. Connect it to the unmatched node.
        else if (previousLabel is not null)
        {
            CfgNode.Connect(previousLabel!, switchHelper.UnmatchedNode);
            previousLabel!.WhenFalse = switchHelper.UnmatchedNode;
        }

        // Step 3: construct the body of the case, knowing the continuation context.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            ContinuationContext = continuationForBody,
            BreakContext = switchHelper.Continuation,
        };

        branchBody = Construct(current.Body, sense, newLocalHelper);

        // Step 4: connect all labels to the body.
        foreach (SwitchDecisionNode label in labels)
        {
            CfgNode.Connect(label, branchBody);
            label.WhenTrue = branchBody;

            if (label.IsDefault)
            {
                label.WhenFalse = branchBody;
            }
        }

        return firstLabel ?? switchHelper.UnmatchedNode;
    }

    // private static CfgNode Construct_SwitchBranch(LinkedListNode<CaseStmtNode> @case, ParserIntelliSense sense, ControlFlowHelper localHelper, CfgNode continuation, out SwitchDecisionNode? finalLabel, out CfgNode nextBody)
    // {
    //     CaseStmtNode current = @case.Value;

    //     finalLabel = null;
    //     nextBody = continuation;

    //     bool atFinalLabel = @case.Next is null;

    //     CfgNode nextNode = continuation;
    //     // If next is null, then this is the final label.
    //     if (!atFinalLabel)
    //     {
    //         CfgNode nextLabel = Construct_SwitchBranch(@case.Next!, sense, localHelper, continuation, out finalLabel, out nextBody);
    //         nextNode = nextLabel;
    //     }

    //     // Set up such that continuation context (i.e. fall-through) is the next body node, and break context is the continuation.
    //     ControlFlowHelper newLocalHelper = new(localHelper)
    //     {
    //         ContinuationContext = nextBody,
    //         BreakContext = continuation,
    //     };

    //     // Construct the body of the case.
    //     CfgNode body = Construct(current.Body, sense, newLocalHelper);

    //     for (LinkedListNode<CaseLabelNode>? node = current.Labels.Last; node != null; node = node.Previous)
    //     {
    //         SwitchDecisionNode decision = new(node.Value, localHelper.Scope);

    //         // Connect the decision to the body.
    //         CfgNode.Connect(decision, body);
    //         decision.WhenTrue = body;

    //         // If this is the final label of the switch statement, mark it accordingly.
    //         if (atFinalLabel)
    //         {
    //             finalLabel = decision;
    //             atFinalLabel = false;
    //         }

    //         // Connect the decision to the following label/continuation.
    //         CfgNode.Connect(decision, nextNode);
    //         decision.WhenFalse = nextNode;

    //         CaseLabelNode label = node.Value;

    //         // If we've found the default label, mark it accordingly and ensure
    //         // the final label is connected to it as a when-false condition.
    //         if (label.NodeType == AstNodeType.DefaultLabel)
    //         {
    //             // Replace the final label's when-false condition with the default body.
    //             // Final label is guaranteed to be non-null at this point.
    //             CfgNode.Disconnect(finalLabel!, finalLabel!.WhenFalse);

    //             CfgNode.Connect(finalLabel, body);
    //             finalLabel.WhenFalse = body;
    //         }

    //         nextNode = decision;
    //     }

    //     nextBody = body;
    //     return nextNode; // Which is the first label that belongs to this case.
    // }

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