using GSCode.Parser.CFA;
using GSCode.Parser.Data;

namespace GSCode.Parser.DFA;

internal ref struct SemanticSenseGenerator(
    List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, 
    ParserIntelliSense sense, 
    Dictionary<string, IExportedSymbol> exportedSymbolTable,
    ReachingDefinitionsAnalyser reachingDefinitionsAnalyser)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;

    private readonly ReachingDefinitionsAnalyser _reachingDefinitionsAnalyser = reachingDefinitionsAnalyser;

    public void Run()
    {
        
    }
}

