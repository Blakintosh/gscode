using GSCode.Parser.Data;
using GSCode.Parser.Steps.Interfaces;

namespace GSCode.Parser.Steps;

#if PREVIEW

internal class ControlFlowAnalyserStep : IParserStep, ISenseProvider
{

    public ParserIntelliSense Sense { get; }
    public DefinitionsTable DefinitionsTable { get; }

    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = new();

    public ControlFlowAnalyserStep(ParserIntelliSense sense, DefinitionsTable definitionsTable)
    {
        Sense = sense;
        DefinitionsTable = definitionsTable;
    }

    public void Run()
    {
        // Evaluate & analyse all bodies
        foreach(Tuple<ScrFunction, ASTBranch> pair in DefinitionsTable.LocalScopedFunctions)
        {
            // Produce a CFG for the function
            ControlFlowGraph functionGraph = ControlFlowGraph.ConstructFunctionGraph(pair.Item2, Sense);

            // Add the CFG to the list
            FunctionGraphs.Add(new(pair.Item1, functionGraph));
        }
    }
}

#endif