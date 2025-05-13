using GSCode.Parser.CFA;
using GSCode.Parser.Data;

namespace GSCode.Parser.DFA;

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;

    public void Run()
    {
        foreach (Tuple<ScrFunction, ControlFlowGraph> functionGraph in FunctionGraphs)
        {
            ForwardAnalyse(functionGraph.Item1, functionGraph.Item2);
        }
    }

    public void ForwardAnalyse(ScrFunction function, ControlFlowGraph functionGraph)
    {
        // Dictionary<BasicBlock, Dictionary<string, ScrVariable>> inSets = new();
        // Dictionary<BasicBlock, Dictionary<string, ScrVariable>> outSets = new();

        // Stack<BasicBlock> worklist = new();
        // worklist.Push(functionGraph.Start);

        // while (worklist.Count > 0)
        // {
        //     BasicBlock node = worklist.Pop();

        //     // Calculate the in set
        //     Dictionary<string, ScrVariable> inSet = new();
        //     foreach (BasicBlock incoming in node.Incoming)
        //     {
        //         if (outSets.TryGetValue(incoming, out Dictionary<string, ScrVariable>? value))
        //         {
        //             inSet.MergeTables(value, node.Scope);
        //         }
        //     }

        //     // Check if the in set has changed, if not, then we can skip this node.
        //     if (inSets.TryGetValue(node, out Dictionary<string, ScrVariable>? currentInSet) && currentInSet.VariableTableEquals(inSet))
        //     {
        //         continue;
        //     }

        //     // Update the in & out sets
        //     inSets[node] = inSet;

        //     if (!outSets.ContainsKey(node))
        //     {
        //         outSets[node] = new Dictionary<string, ScrVariable>();
        //     }

        //     // Calculate the out set
        //     if (node.Type == ControlFlowType.FunctionEntry)
        //     {
        //         outSets[node].MergeTables(inSet, node.Scope);
        //     }
        //     else
        //     {
        //         SymbolTable symbolTable = new(ExportedSymbolTable, inSet, node.Scope);

        //         // TODO: Unioning of sets is not ideal, better to merge the ScrDatas of common key across multiple dictionaries. Easier to use with the symbol tables.
        //         // TODO: Analyse statement-by-statement, using the analysers already created, and get the out set.
        //         //Analyse(node, symbolTable, inSets, outSets, Sense);
        //         //outSet.UnionWith(symbolTable.GetOutgoingSymbols());
        //         AnalyseBasicBlock(node, symbolTable);

        //         outSets[node] = symbolTable.VariableSymbols;
        //     }

        //     // Add the successors to the worklist
        //     foreach (BasicBlock successor in node.Outgoing)
        //     {
        //         worklist.Push(successor);
        //     }
        // }
    }

}