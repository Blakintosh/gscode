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
using GSCode.Parser.CFA.Nodes;
using System.Runtime.InteropServices;

namespace GSCode.Parser.Steps;

internal class ControlFlowAnalyserStep : IParserStep, ISenseProvider
{

    public ParserIntelliSense Sense { get; }
    public DefinitionsTable DefinitionsTable { get; }

    public List<Tuple<ScrFunction, ControlFlowNode>> FunctionGraphs { get; } = new();

    public ControlFlowAnalyserStep(ParserIntelliSense sense, DefinitionsTable definitionsTable)
    {
        Sense = sense;
        DefinitionsTable = definitionsTable;
    }

    public async Task RunAsync()
    {
        // Evaluate & analyse all bodies
        await Task.Run(() =>
        {
            foreach(Tuple<ScrFunction, ASTBranch> pair in DefinitionsTable.LocalScopedFunctions)
            {
                // Produce a CFG for the function
                Span<ASTNode> nodeStream = CollectionsMarshal.AsSpan(pair.Item2.Children);
                ControlFlowNode cfg = ControlFlowNode.Construct_Standard(nodeStream, new(default, default), default, Sense);

                // Add the CFG to the list
                FunctionGraphs.Add(new(pair.Item1, cfg));
            }
        });
    }
}
