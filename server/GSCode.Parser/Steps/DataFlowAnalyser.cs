
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.DFA;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.Steps.Interfaces;
using GSCode.Parser.Util;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.Steps;

//internal class DataFlowAnalyser : IParserStep, ISenseProvider
//{
//    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = new();
//    public ParserIntelliSense Sense { get; }
//    private Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = new();

//    public DataFlowAnalyser(ParserIntelliSense sense, IEnumerable<IExportedSymbol> exportedSymbols, List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs)
//    {
//        Sense = sense;
//        FunctionGraphs = functionGraphs;

//        // TODO: and checked for duplicates.
//        // Does it need to be checked for duplicates? I don't think so. Dependent-level analysis should automatically sift thru duplicates.
//        foreach (IExportedSymbol symbol in exportedSymbols)
//        {
//            if (ExportedSymbolTable.ContainsKey(symbol.Name))
//            {
//                //throw new Exception($"Duplicate symbol {symbol.Name} found in symbol table.");
//                //Log.Warning($"Duplicate symbol {symbol.Name}");
//                continue;
//            }
//            ExportedSymbolTable.Add(symbol.Name, symbol);
//        }
//    }


//    public void Run()
//    {
//        foreach (Tuple<ScrFunction, ControlFlowGraph> pair in FunctionGraphs)
//        {
//            ForwardAnalyse(pair.Item1, pair.Item2);
//        }
//    }

//    public void ForwardAnalyse(ScrFunction function, ControlFlowGraph functionGraph)
//    {
//        Dictionary<BasicBlock, Dictionary<string, ScrVariable>> inSets = new();
//        Dictionary<BasicBlock, Dictionary<string, ScrVariable>> outSets = new();

//        Stack<BasicBlock> worklist = new();
//        worklist.Push(functionGraph.Start);

//        while (worklist.Count > 0)
//        {
//            BasicBlock node = worklist.Pop();

//            // Calculate the in set
//            Dictionary<string, ScrVariable> inSet = new();
//            foreach (BasicBlock incoming in node.Incoming)
//            {
//                if (outSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? value))
//                {
//                    inSet.MergeTables(value, node.Scope);
//                }
//            }

//            // Check if the in set has changed, if not, then we can skip this node.
//            if(inSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
//            {
//                continue;
//            }

//            // Update the in & out sets
//            inSets[node] = inSet;

//            if (!outSets.ContainsKey(node))
//            {
//                outSets[node] = new Dictionary<string, ScrVariable>();
//            }

//            // Calculate the out set
//            if (node.Type == ControlFlowType.FunctionEntry)
//            {
//                outSets[node].MergeTables(inSet, node.Scope); 
//            }
//            else
//            {
//                SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

//                // TODO: Unioning of sets is not ideal, better to merge the ScrDatas of common key across multiple dictionaries. Easier to use with the symbol tables.
//                // TODO: Analyse statement-by-statement, using the analysers already created, and get the out set.
//                //Analyse(node, symbolTable, inSets, outSets, Sense);
//                //outSet.UnionWith(symbolTable.GetOutgoingSymbols());
//                AnalyseBasicBlock(node, symbolTable);

//                outSets[node] = symbolTable.VariableSymbols;
//            }

//            // Add the successors to the worklist
//            foreach (BasicBlock successor in node.Outgoing)
//            {
//                worklist.Push(successor);
//            }
//        }
//    }

//    public void AnalyseBasicBlock(BasicBlock block, SymbolTable symbolTable)
//    {
//        ReadOnlyCollection<ASTNode> logic = block.Logic;


//        for (int i = 0; i < logic.Count; i++)
//        {
//            ASTNode child = logic[i];

//            ASTNode? last = i - 1 >= 0 ? logic[i - 1] : null;
//            ASTNode? next = i + 1 < logic.Count ? logic[i + 1] : null;

//            // Analyse the child for a signature
//            if (child.Analyser is DataFlowNodeAnalyser analyser)
//            {
//                analyser.Analyse(child, last, next, symbolTable, Sense);
//            }
//        }
//    }
//}
