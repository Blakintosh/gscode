using System.Collections.ObjectModel;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SA;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;

namespace GSCode.Parser.DFA;

internal ref struct ControlFlowAnalyser(ParserIntelliSense sense, DefinitionsTable definitionsTable)
{
    public ParserIntelliSense Sense { get; } = sense;
    public DefinitionsTable DefinitionsTable { get; } = definitionsTable;

    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = new();

    public void Run()
    {
        // Evaluate & analyse all bodies
        foreach(Tuple<ScrFunction, FunDefnNode> pair in DefinitionsTable.LocalScopedFunctions)
        {
            // Produce a CFG for the function
            ControlFlowGraph functionGraph = ControlFlowGraph.ConstructFunctionGraph(pair.Item2, Sense);

            // Add the CFG to the list
            FunctionGraphs.Add(new(pair.Item1, functionGraph));
        }
    }
}