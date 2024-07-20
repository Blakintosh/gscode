using GSCode.Parser.AST.Nodes;
using GSCode.Parser.Data;
using GSCode.Parser.SPA.Logic.Analysers;
using GSCode.Parser.SPA.Logic.Components;
using GSCode.Parser.SPA;
using GSCode.Parser.Steps.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using GSCode.Parser.CFA;

namespace GSCode.Parser.Steps;

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
