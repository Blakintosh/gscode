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

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, ControlFlowGraph>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null, string? currentNamespace = null, HashSet<string>? knownNamespaces = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, ControlFlowGraph>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;
    public string? CurrentNamespace { get; } = currentNamespace;
    public HashSet<string>? KnownNamespaces { get; } = knownNamespaces;

    public void Run()
    {
        ReachingDefinitionsAnalyser reachingDefinitionsAnalyser = new(FunctionGraphs, ClassGraphs, Sense, ExportedSymbolTable, ApiData, CurrentNamespace, KnownNamespaces);
        reachingDefinitionsAnalyser.Run();

        SemanticSenseGenerator semanticSenseGenerator = new(FunctionGraphs, Sense, ExportedSymbolTable, reachingDefinitionsAnalyser);
        semanticSenseGenerator.Run();
    }
}