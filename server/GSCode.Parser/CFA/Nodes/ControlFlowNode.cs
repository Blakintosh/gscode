using GSCode.Data;
using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace GSCode.Parser.CFA.Nodes;

internal class ControlFlowHelper
{
    /// <summary>
    /// For use when breaking out of a loop.
    /// </summary>
    public ControlFlowNode? NearestLoopEnd { get; }
    /// <summary>
    /// For use when continuing a loop.
    /// </summary>
    public ControlFlowNode? NearestLoopCondition { get; }
    /// <summary>
    /// The current code is unreachable.
    /// </summary>
    public bool InUnreachableCode { get; }

    public ControlFlowHelper(ControlFlowNode? nearestLoopEnd, ControlFlowNode? nearestLoopCondition)
    {
        NearestLoopEnd = nearestLoopEnd;
        NearestLoopCondition = nearestLoopCondition;
    }

    private ControlFlowHelper()
    {
        InUnreachableCode = true;
    }

    public static ControlFlowHelper UnreachableCode { get; } = new();
}

internal enum ControlFlowType
{
    Loop, // Loop statements
    If, // If, else-if statements
    Switch, // Switch statements
    Logic // Standard logic
}

internal partial class ControlFlowNode
{
    /// <summary>
    /// The incoming edges to this node.
    /// </summary>
    public List<ControlFlowNode> Predecessors { get; } = new();
    /// <summary>
    /// The outgoing edges from this node.
    /// </summary>
    public List<ControlFlowNode> Successors { get; } = new();

    public ASTNode? DecisionNode { get; }

    public ControlFlowType Type { get; }

    /// <summary>
    /// The AST nodes that form the logic of this branch.
    /// </summary>
    public List<ASTNode> Logic { get; } = new();

    /// <summary>
    /// Whether this branch should not connect to the next branch.
    /// </summary>
    public bool Severed { get; private set; } = false;

    private ControlFlowNode(List<ASTNode> logic, ControlFlowNode? successor)
    {
        Type = ControlFlowType.Logic;
        Logic = logic;
        if(successor is not null)
        {
            Successors.Add(successor);
        }
    }

    private ControlFlowNode(ASTNode decisionNode, ControlFlowType type, IEnumerable<ControlFlowNode> successors)
    {
        Type = type;
        DecisionNode = decisionNode;
        Successors = successors.ToList();
    }

    private ControlFlowNode(ASTNode decisionNode, ControlFlowType type)
    {
        Type = type;
        DecisionNode = decisionNode;
    }

    public static ControlFlowNode Construct_Standard(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode? parentContinuationNode, ParserIntelliSense sense)
    {
        List<ASTNode> logic = new();

        bool inUnreachableCode = context.InUnreachableCode;
        ControlFlowNode? next = parentContinuationNode;

        ControlFlowHelper successorContext = context;

        int i = 0;
        while(i < nodesStream.Length)
        {
            ASTNode node = nodesStream[i];

            switch(node.Type)
            {
                case NodeTypes.IfStatement:
                {
                    ControlFlowNode connection = Construct_IfStatement(nodesStream[i..], successorContext, parentContinuationNode, sense);
                    return new(logic, connection);
                }
                case NodeTypes.WhileLoop:
                case NodeTypes.ForLoop:
                case NodeTypes.ForeachLoop:
                {
                    ControlFlowNode connection = Construct_LoopStatement(nodesStream[i..], successorContext, parentContinuationNode, sense);
                    return new(logic, connection);
                }
                case NodeTypes.DoLoop:
                {
                    ControlFlowNode connection = Construct_DoLoopStatement(nodesStream[i..], successorContext, parentContinuationNode, sense);
                    return new(logic, connection);
                }
                case NodeTypes.BreakStatement:
                    if (inUnreachableCode)
                    {
                        break;
                    }
                    if (context.NearestLoopEnd is not null)
                    {
                        //if(parentContinuationNode is not null)
                        //{
                        //    context.NearestLoopEnd.Predecessors.Add(parentContinuationNode);
                        //}

                        // Check for remaining code and mark as unreachable
                        if(i + 1 < nodesStream.Length)
                        {
                            Position start = nodesStream[i + 1].TextRange.Start;
                            Position end = nodesStream[^1].TextRange.End;

                            // WARNING: Unreachable code detected
                            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
                            {
                                Start = start,
                                End = end
                            }, DiagnosticSources.SPA, GSCErrorCodes.UnreachableCodeDetected));
                        }

                        next = context.NearestLoopEnd;
                        inUnreachableCode = true;
                        successorContext = ControlFlowHelper.UnreachableCode;
                        break;
                    }

                    // ERROR: Break statement outside of loop
                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.TextRange, DiagnosticSources.SPA, GSCErrorCodes.NoEnclosingLoop));
                    break;

                case NodeTypes.ContinueStatement:
                    if (inUnreachableCode)
                    {
                        break;
                    }
                    if (context.NearestLoopCondition is not null)
                    {
                        //if(parentContinuationNode is not null)
                        //{
                        //    context.NearestLoopEnd.Predecessors.Add(parentContinuationNode);
                        //}

                        // Check for remaining code and mark as unreachable
                        if (i + 1 < nodesStream.Length)
                        {
                            Position start = nodesStream[i + 1].TextRange.Start;
                            Position end = nodesStream[^1].TextRange.End;

                            // WARNING: Unreachable code detected
                            sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(new Range
                            {
                                Start = start,
                                End = end
                            }, DiagnosticSources.SPA, GSCErrorCodes.UnreachableCodeDetected));
                        }

                        next = context.NearestLoopCondition;
                        inUnreachableCode = true;
                        successorContext = ControlFlowHelper.UnreachableCode;
                        break;
                    }

                    // ERROR: Continue statement outside of loop
                    sense.Diagnostics.Add(DiagnosticCodes.GetDiagnostic(node.TextRange, DiagnosticSources.SPA, GSCErrorCodes.NoEnclosingLoop));
                    break;
                default:
                    logic.Add(node);
                    break;
            }
            i++;
        }
        // At the end of the block & we have no control flow, so connect as necessary
        return new(logic, next);
    }

    /// <summary>
    /// Produces a CFG node for an if statement.
    /// </summary>
    /// <param name="nodesStream">The current partition of the node stream</param>
    /// <param name="context">Control flow context</param>
    /// <returns></returns>
    public static ControlFlowNode Construct_IfStatement(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode? parentContinuationNode, ParserIntelliSense sense)
    {
        ASTNode decisionNode = nodesStream[0];

        // Get the start of the continuation
        int i = 1;
        while (i < nodesStream.Length &&
            (nodesStream[i].Type == NodeTypes.ElseIfStatement || nodesStream[i].Type == NodeTypes.ElseStatement))
        {
            i++;
        }

        // Continuation node
        ControlFlowNode continuationNode = Construct_Standard(nodesStream[i..], context, parentContinuationNode, sense);

        // Then node
        ControlFlowNode then = Construct_Standard(CollectionsMarshal.AsSpan(decisionNode.Branch!.Children), context, continuationNode, sense);

        if (!then.Severed)
        {
            then.Successors.Add(continuationNode);
        }

        ControlFlowNode next = continuationNode;

        // Further else-if/else nodes
        if (nodesStream.Length > 1)
        {
            ASTNode nextNode = nodesStream[1];

            if (nextNode.Type == NodeTypes.ElseIfStatement)
            {
                next = Construct_ElseIfStatement(nodesStream[1..], context, continuationNode, sense);
            }
            else if (nextNode.Type == NodeTypes.ElseStatement)
            {
                next = Construct_ElseStatement(nodesStream[1..], context, continuationNode, sense);
            }
        }

        // Continuation as a third successor does not truly exist, but is used to provide a permitted entry path for continuation
        return new(decisionNode, ControlFlowType.If, new[] { then, next, continuationNode });
    }

    /// <summary>
    /// Produces a CFG node for an else-if statement.
    /// </summary>
    /// <param name="nodesStream">The current partition of the node stream</param>
    /// <param name="context">Control flow context</param>
    /// <param name="continuationNode">The continuation node to connect to</param>
    /// <returns></returns>
    public static ControlFlowNode Construct_ElseIfStatement(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode continuationNode, ParserIntelliSense sense)
    {
        ASTNode decisionNode = nodesStream[0];

        // Then node
        ControlFlowNode then = Construct_Standard(CollectionsMarshal.AsSpan(decisionNode.Branch!.Children), context, continuationNode, sense);

        ControlFlowNode next = continuationNode;
        // Further else-if/else nodes
        if (nodesStream.Length > 1)
        {
            ASTNode nextNode = nodesStream[1];

            if(nextNode.Type == NodeTypes.ElseIfStatement)
            {
                next = Construct_ElseIfStatement(nodesStream[1..], context, continuationNode, sense);
            }
            else if(nextNode.Type == NodeTypes.ElseStatement)
            {
                next = Construct_ElseStatement(nodesStream[1..], context, continuationNode, sense);
            }
        }

        return new(decisionNode, ControlFlowType.If, new[] { then, next });
    }

    /// <summary>
    /// Produces a CFG node for an else statement.
    /// </summary>
    /// <param name="nodesStream">The current partition of the node stream</param>
    /// <param name="context">Control flow context</param>
    /// <param name="continuationNode">The continuation node to connect to</param>
    /// <returns></returns>
    public static ControlFlowNode Construct_ElseStatement(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode continuationNode, ParserIntelliSense sense)
    {
        ASTNode decisionNode = nodesStream[0];

        // Body node
        ControlFlowNode body = Construct_Standard(CollectionsMarshal.AsSpan(decisionNode.Branch!.Children), context, continuationNode, sense);

        return body;
    }

    /// <summary>
    /// Produces a CFG node for a loop statement.
    /// </summary>
    /// <param name="nodesStream">The current partition of the node stream</param>
    /// <param name="context">Control flow context</param>
    /// <param name="parentContinuationNode">The continuation node to connect to</param>
    /// <returns></returns>
    public static ControlFlowNode Construct_LoopStatement(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode? parentContinuationNode, ParserIntelliSense sense)
    {
        ASTNode decisionNode = nodesStream[0];

        ControlFlowNode decision = new(decisionNode, ControlFlowType.Loop);

        // Continuation node
        ControlFlowNode continuationNode = Construct_Standard(nodesStream[1..], context, parentContinuationNode, sense);

        // Create loop context
        ControlFlowHelper loopContext = new(continuationNode, decision);

        // Body node
        ControlFlowNode body = Construct_Standard(CollectionsMarshal.AsSpan(decisionNode.Branch!.Children), loopContext, continuationNode, sense);

        // Connect the decision node
        decision.Successors.Add(body);
        decision.Successors.Add(continuationNode);

        return decision;
    }

    public static ControlFlowNode Construct_DoLoopStatement(Span<ASTNode> nodesStream, ControlFlowHelper context, ControlFlowNode? parentContinuationNode, ParserIntelliSense sense)
    {
        if(nodesStream.Length < 2)
        {
            // TODO: Not sure if anything has checked already if the 'while' is present.
            throw new InvalidOperationException("Do loop statement is missing the 'while' condition.");
        }

        // The decision node is the 'while' that comes after.
        ASTNode bodyNode = nodesStream[0];
        ASTNode decisionNode = nodesStream[1];

        ControlFlowNode decision = new(decisionNode, ControlFlowType.Loop);

        // Continuation node
        ControlFlowNode continuationNode = Construct_Standard(nodesStream[2..], context, parentContinuationNode, sense);

        // Create loop context
        ControlFlowHelper loopContext = new(continuationNode, decision);

        // Body node
        ControlFlowNode body = Construct_Standard(CollectionsMarshal.AsSpan(bodyNode.Branch!.Children), loopContext, continuationNode, sense);

        // Connect the decision node
        decision.Successors.Add(body);
        decision.Successors.Add(continuationNode);

        return body;
    }
}
