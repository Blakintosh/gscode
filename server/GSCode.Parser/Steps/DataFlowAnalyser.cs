using GSCode.Parser.AST.Nodes;
using GSCode.Parser.CFA.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA.Sense;
using GSCode.Parser.Steps.Interfaces;
using GSCode.Parser.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Steps;

internal readonly ref struct DataFlowContext
{

    public ControlFlowNode ContinuationNode { get; init; }
    public ControlFlowNode LoopBackNode { get; init; }
}

internal class DataFlowAnalyser : IParserStep, ISenseProvider
{
    public List<Tuple<ScrFunction, ControlFlowNode>> FunctionGraphs { get; } = new();
    public ParserIntelliSense Sense { get; }
    public SymbolTable RootSymbolTable { get; }

    public DataFlowAnalyser(ParserIntelliSense sense, IEnumerable<IExportedSymbol> exportedSymbols, List<Tuple<ScrFunction, ControlFlowNode>> functionGraphs)
    {
        Sense = sense;
        FunctionGraphs = functionGraphs;
        RootSymbolTable = new(exportedSymbols);
    }


    public async Task RunAsync()
    {
        await Task.Run(() =>
        {
            foreach (Tuple<ScrFunction, ControlFlowNode> pair in FunctionGraphs)
            {
                // Analyse the CFG
                ControlFlowNode cfg = pair.Item2;

                Analyse(cfg, RootSymbolTable, new(), new(), Sense);
            }
        });
    }



    public static void Analyse(ControlFlowNode node, Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
        DataFlowContext context, ParserIntelliSense sense)
    {
        // This is a deferred node, we're now analysing it.
        SymbolTable symbolTable = deferredNodes.Pop(node);

        Analyse(node, symbolTable, deferredNodes, context, sense);
    }

    public static void Analyse(ControlFlowNode node, SymbolTable symbolTable,
               DataFlowContext context, ParserIntelliSense sense)
    {
        Analyse(node, symbolTable, new Dictionary<ControlFlowNode, SymbolTable>(), context, sense);
    }

    public static void Analyse(ControlFlowNode node, SymbolTable symbolTable,
               Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
                      DataFlowContext context, ParserIntelliSense sense)
    {
        switch (node.Type)
        {
            case ControlFlowType.Logic:
                Analyse_Logic(node, symbolTable, deferredNodes, context, sense);
                break;
            case ControlFlowType.If:
                Analyse_If(node, symbolTable, deferredNodes, context, sense);
                break;
            case ControlFlowType.Loop:
                Analyse_Loop(node, symbolTable, deferredNodes, context, sense);
                break;
            case ControlFlowType.Switch:
                Analyse_Switch(node, symbolTable, deferredNodes, context, sense);
                break;
        }
    }

    private static void Analyse_Logic(ControlFlowNode node, SymbolTable symbolTable,
        Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
        DataFlowContext context, ParserIntelliSense sense)
    {
        for (int i = 0; i < node.Logic.Count; i++)
        {
            ASTNode currentNode = node.Logic[i];
            if (currentNode.Analyser is DataFlowNodeAnalyser analyser)
            {
                ASTNode? previous = i - 1 > 0 ? node.Logic[i - 1] : null;
                ASTNode? next = i + 1 < node.Logic.Count ? node.Logic[i + 1] : null;

                analyser.Analyze(currentNode, previous, next, symbolTable, sense);
            }
        }

        bool hasNextNode = node.Successors.Count > 0;
        if (hasNextNode)
        {
            ControlFlowNode nextNode = node.Successors[0];

            if (context.ContinuationNode == nextNode || context.LoopBackNode == nextNode)
            {
                SymbolTable nodeSymbolTable = deferredNodes[nextNode];
                nodeSymbolTable.AddIncomingSymbols(symbolTable);

                return;
            }
            Analyse(nextNode, symbolTable, deferredNodes, context, sense);
        }
    }

    private static void Analyse_If(ControlFlowNode node, SymbolTable symbolTable,
        Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
        DataFlowContext context, ParserIntelliSense sense)
    {
        if(node.Successors.Count == 3)
        {
            symbolTable.MarkSymbolsAsSplit();

            // Label the continuation, so that none of the other branches enter it
            ControlFlowNode continuation = node.Successors[2];
            DataFlowContext newContext = context with { ContinuationNode = continuation };
            deferredNodes[continuation] = new(symbolTable, symbolTable.Depth);

            // Enter then
            ControlFlowNode then = node.Successors[0];
            Analyse(then, symbolTable.Clone(), deferredNodes, newContext, sense);

            // Enter else
            ControlFlowNode _else = node.Successors[1];
            Analyse(_else, symbolTable.Clone(), deferredNodes, newContext, sense);

            symbolTable.UnmarkSymbolsAsSplit();

            // Enter continuation
            SymbolTable nodeSymbolTable = deferredNodes.Pop(continuation);
            Analyse(continuation, nodeSymbolTable, deferredNodes, context, sense);
            return;
        }

        // Enter then
        ControlFlowNode thenNode = node.Successors[0];
        Analyse(thenNode, symbolTable.Clone(), deferredNodes, context, sense);

        // Enter else
        ControlFlowNode elseNode = node.Successors[1];
        Analyse(elseNode, symbolTable.Clone(), deferredNodes, context, sense);
    }

    private static void Analyse_Loop(ControlFlowNode node, SymbolTable symbolTable,
        Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
        DataFlowContext context, ParserIntelliSense sense)
    {
        symbolTable.MarkSymbolsAsSplit();

        // Label the loop back & continuations, so that none of the other branches enter them
        ControlFlowNode loopBack = node;
        ControlFlowNode continuation = node.Successors[1];

        DataFlowContext newContext = context with { LoopBackNode = loopBack, ContinuationNode = continuation };
        if (!deferredNodes.ContainsKey(loopBack))
        {
            deferredNodes[loopBack] = new(symbolTable, symbolTable.Depth);
        }
        deferredNodes[continuation] = new(symbolTable, symbolTable.Depth);

        // Enter body
        ControlFlowNode body = node.Successors[0];
        Analyse(body, symbolTable.Clone(symbolTable.Depth + 1), deferredNodes, newContext, sense);

        symbolTable.UnmarkSymbolsAsSplit();

        // Enter continuation
        SymbolTable nodeSymbolTable = deferredNodes.Pop(continuation);
        Analyse(continuation, nodeSymbolTable, deferredNodes, context, sense);
    }

    private static void Analyse_Switch(ControlFlowNode node, SymbolTable symbolTable,
        Dictionary<ControlFlowNode, SymbolTable> deferredNodes,
        DataFlowContext context, ParserIntelliSense sense)
    {

    }
}
