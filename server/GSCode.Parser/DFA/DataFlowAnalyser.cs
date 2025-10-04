using System.Collections.ObjectModel;
using GSCode.Data;
using GSCode.Parser.AST;
using GSCode.Parser.CFA;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using GSCode.Parser.SPA;
using GSCode.Parser.SPA.Logic.Components;
using Serilog;

namespace GSCode.Parser.DFA;

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;

    public void Run()
    {
        ReachingDefinitionsAnalyser reachingDefinitionsAnalyser = new(FunctionGraphs, Sense, ExportedSymbolTable, ApiData);
        reachingDefinitionsAnalyser.Run();

        SemanticSenseGenerator semanticSenseGenerator = new(FunctionGraphs, Sense, ExportedSymbolTable, reachingDefinitionsAnalyser);
        semanticSenseGenerator.Run();
    }
}