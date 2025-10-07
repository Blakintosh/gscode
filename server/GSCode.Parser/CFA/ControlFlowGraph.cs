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

    public static ControlFlowGraph ConstructFunctionGraph(FunDefnNode node, ParserIntelliSense sense, int scope = 0)
    {
        // Function graph: (entry) -> (body) -> (exit)

        // Create the entry and exit blocks, and the function graph
        FunEntryBlock entry = new(node, node.Name);
        FunExitBlock exit = new(node);

        ControlFlowHelper functionHelper = new()
        {
            ReturnContext = exit,
            ContinuationContext = exit,
            Scope = scope
        };

        // Construct the function graph.
        LinkedListNode<AstNode>? currentNode = node.Body.Statements.First;
        CfgNode body = Construct(ref currentNode, sense, functionHelper);

        CfgNode.Connect(entry, body);

        return new(entry, exit);
    }

    public static ControlFlowGraph ConstructClassGraph(ClassDefnNode node, ParserIntelliSense sense)
    {
        // Class graph: (entry) -> (body) -> (exit)

        // Create the entry and exit blocks
        ClassEntryBlock entry = new(node, node.NameToken);
        ClassExitBlock exit = new(node);

        // Construct the class body graph
        LinkedListNode<AstNode>? currentNode = node.Body.Definitions.First;
        CfgNode? body = Construct_InClassBody(ref currentNode, sense);

        // If we have a body, connect it; otherwise connect directly to exit
        if (body is not null)
        {
            CfgNode.Connect(entry, body);
        }
        else
        {
            CfgNode.Connect(entry, exit);
        }

        return new(entry, exit);
    }

    private static CfgNode? Construct_InClassBody(AstNode node, ParserIntelliSense sense)
    {
        // This is cheesy, but should work.
        LinkedListNode<AstNode>? currentNode = new LinkedListNode<AstNode>(node);

        return Construct_InClassBody(ref currentNode, sense);
    }

    private static CfgNode? Construct_InClassBody(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense)
    {
        // If we're at the end of the current block, return null (no continuation)
        if (currentNode is null)
        {
            return null;
        }

        return currentNode.Value.NodeType switch
        {
            AstNodeType.Constructor => Construct_ClassConstructor(ref currentNode, sense),
            AstNodeType.Destructor => Construct_ClassDestructor(ref currentNode, sense),
            AstNodeType.FunctionDefinition => Construct_ClassMethod(ref currentNode, sense),
            _ => Construct_ClassBlock(ref currentNode, sense),
        };
    }

    private static ClassMembersBlock Construct_ClassBlock(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense)
    {
        // Class members block: collect consecutive member declarations until we hit a method/constructor/destructor
        LinkedList<AstNode> members = new();

        while (currentNode != null && !IsClassMethodNode(currentNode.Value))
        {
            members.AddLast(currentNode.Value);
            currentNode = currentNode.Next;
        }

        ClassMembersBlock membersBlock = new(members, 0);

        // If we reached the end of the class body, just return the members block
        if (currentNode is null)
        {
            return membersBlock;
        }

        // If we hit a method/constructor/destructor, construct it and connect
        CfgNode? nextNode = Construct_InClassBody(ref currentNode, sense);

        if (nextNode is not null)
        {
            CfgNode.Connect(membersBlock, nextNode);
        }

        return membersBlock;
    }

    private static CfgNode? Construct_ClassConstructor(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense)
    {
        StructorDefnNode constructor = (StructorDefnNode)currentNode!.Value;

        // Move to the next node to get the continuation
        currentNode = currentNode.Next;
        CfgNode? continuation = Construct_InClassBody(ref currentNode, sense);

        // Create entry and exit blocks for the constructor
        // Constructor uses scope 1 (class members are at scope 0, constructor body is at scope 1)
        FunEntryBlock entry = new(null!, constructor.KeywordToken);
        FunExitBlock exit = new(constructor);

        ControlFlowHelper constructorHelper = new()
        {
            ReturnContext = exit,
            ContinuationContext = exit,
            Scope = 1  // Class body is scope 0, constructor body is scope 1
        };

        // Construct the constructor body graph
        LinkedListNode<AstNode>? bodyNode = constructor.Body.Statements.First;
        CfgNode body = Construct(ref bodyNode, sense, constructorHelper);

        CfgNode.Connect(entry, body);

        // Connect exit to continuation (next node in class body)
        if (continuation is not null)
        {
            CfgNode.Connect(exit, continuation);
        }

        return entry;
    }

    private static CfgNode? Construct_ClassDestructor(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense)
    {
        StructorDefnNode destructor = (StructorDefnNode)currentNode!.Value;

        // Move to the next node to get the continuation
        currentNode = currentNode.Next;
        CfgNode? continuation = Construct_InClassBody(ref currentNode, sense);

        // Create entry and exit blocks for the destructor
        // Destructor uses scope 1 (class members are at scope 0, destructor body is at scope 1)
        FunEntryBlock entry = new(null!, destructor.KeywordToken);
        FunExitBlock exit = new(destructor);

        ControlFlowHelper destructorHelper = new()
        {
            ReturnContext = exit,
            ContinuationContext = exit,
            Scope = 1  // Class body is scope 0, destructor body is scope 1
        };

        // Construct the destructor body graph
        LinkedListNode<AstNode>? bodyNode = destructor.Body.Statements.First;
        CfgNode body = Construct(ref bodyNode, sense, destructorHelper);

        CfgNode.Connect(entry, body);

        // Connect exit to continuation (next node in class body)
        if (continuation is not null)
        {
            CfgNode.Connect(exit, continuation);
        }

        return entry;
    }

    private static CfgNode? Construct_ClassMethod(ref LinkedListNode<AstNode>? currentNode, ParserIntelliSense sense)
    {
        FunDefnNode method = (FunDefnNode)currentNode!.Value;

        // Move to the next node to get the continuation
        currentNode = currentNode.Next;
        CfgNode? continuation = Construct_InClassBody(ref currentNode, sense);

        // Construct the function graph with scope 1
        // Class members are at scope 0, method body is at scope 1
        ControlFlowGraph methodGraph = ConstructFunctionGraph(method, sense, scope: 1);

        // Connect exit to continuation (next node in class body)
        if (continuation is not null)
        {
            CfgNode.Connect(methodGraph.End, continuation);
        }

        return methodGraph.Start;
    }

    private static bool IsClassMethodNode(AstNode node)
    {
        return node.NodeType == AstNodeType.Constructor ||
               node.NodeType == AstNodeType.Destructor ||
               node.NodeType == AstNodeType.FunctionDefinition;
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
        CfgNode firstLabel = Construct_SwitchBranch(switchAstNode.Cases.Cases.First!, switchNode, sense, localHelper, switchHelper, out _);

        CfgNode.Connect(switchNode, firstLabel);
        switchNode.FirstLabel = firstLabel;

        return switchNode;
    }

    private static CfgNode Construct_SwitchBranch(LinkedListNode<CaseStmtNode> @case, SwitchNode switchNode, ParserIntelliSense sense, ControlFlowHelper localHelper, SwitchHelper switchHelper, out CfgNode branchBody)
    {
        CaseStmtNode current = @case.Value;

        // Check for duplicate default labels within this case
        bool hasDefault = false;
        foreach (CaseLabelNode label in current.Labels)
        {
            if (label.NodeType == AstNodeType.DefaultLabel)
            {
                if (hasDefault || switchHelper.ContainsDefaultLabel)
                {
                    sense.AddSpaDiagnostic(label.Value?.Range ?? label.Keyword.Range, GSCErrorCodes.MultipleDefaultLabels);
                }
                else
                {
                    hasDefault = true;
                    switchHelper.ContainsDefaultLabel = true;
                }
            }
        }

        // Create a single decision node for this case (containing all its labels)
        SwitchCaseDecisionNode caseDecision = new(current, switchNode, hasDefault, localHelper.Scope);

        // Track what the continuation for the body should be, ie in case of fall-through.
        CfgNode continuationForBody = switchHelper.Continuation;

        // Step 1: recurse into other branches, and let them generate their case nodes backwards.
        CfgNode? nextCaseDecision = null;
        if (@case.Next is not null)
        {
            nextCaseDecision = Construct_SwitchBranch(@case.Next!, switchNode, sense, localHelper, switchHelper, out continuationForBody);
        }

        // Step 2: construct the body of the case, knowing the continuation context.
        ControlFlowHelper newLocalHelper = new(localHelper)
        {
            ContinuationContext = continuationForBody,
            BreakContext = switchHelper.Continuation,
        };

        branchBody = Construct(current.Body, sense, newLocalHelper);

        // Step 3: connect this case decision node to the body and next case/continuation
        CfgNode.Connect(caseDecision, branchBody);
        caseDecision.WhenTrue = branchBody;

        // If this case contains only default label(s), it should be the unmatched node
        bool hasOnlyDefault = current.Labels.All(l => l.NodeType == AstNodeType.DefaultLabel);

        if (hasOnlyDefault)
        {
            // Default-only case: set as unmatched node and point WhenFalse to body (unconditional)
            switchHelper.UnmatchedNode = caseDecision;
            caseDecision.WhenFalse = branchBody;

            // Return the next case decision if it exists, otherwise the unmatched node (this node)
            return nextCaseDecision ?? caseDecision;
        }
        else
        {
            // Regular case: connect WhenFalse to next case or unmatched node
            if (nextCaseDecision is not null)
            {
                CfgNode.Connect(caseDecision, nextCaseDecision);
                caseDecision.WhenFalse = nextCaseDecision;
            }
            else
            {
                CfgNode.Connect(caseDecision, switchHelper.UnmatchedNode);
                caseDecision.WhenFalse = switchHelper.UnmatchedNode;
            }

            return caseDecision;
        }
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