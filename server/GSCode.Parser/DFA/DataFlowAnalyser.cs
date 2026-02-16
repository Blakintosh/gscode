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

internal ref struct DataFlowAnalyser(List<Tuple<ScrFunction, ControlFlowGraph>> functionGraphs, List<Tuple<ScrClass, ControlFlowGraph>> classGraphs, ParserIntelliSense sense, Dictionary<string, IExportedSymbol> exportedSymbolTable, ScriptAnalyserData? apiData = null, string? currentNamespace = null, HashSet<string>? knownNamespaces = null, string? fileName = null)
{
    public List<Tuple<ScrFunction, ControlFlowGraph>> FunctionGraphs { get; } = functionGraphs;
    public List<Tuple<ScrClass, ControlFlowGraph>> ClassGraphs { get; } = classGraphs;
    public ParserIntelliSense Sense { get; } = sense;
    public Dictionary<string, IExportedSymbol> ExportedSymbolTable { get; } = exportedSymbolTable;
    public ScriptAnalyserData? ApiData { get; } = apiData;
    public string? CurrentNamespace { get; } = currentNamespace;
    public HashSet<string>? KnownNamespaces { get; } = knownNamespaces;
    public string? FileName { get; } = fileName;

    public void Run()
    {
#if FLAG_PERFORMANCE_TRACKING
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long checkpoint1, checkpoint2;
#endif

        ReachingDefinitionsAnalyser reachingDefinitionsAnalyser = new(FunctionGraphs, ClassGraphs, Sense, ExportedSymbolTable, ApiData, CurrentNamespace, KnownNamespaces, FileName);
        reachingDefinitionsAnalyser.Run();

#if FLAG_PERFORMANCE_TRACKING
        checkpoint1 = sw.ElapsedMilliseconds;
#endif

        SemanticSenseGenerator semanticSenseGenerator = new(FunctionGraphs, Sense, ExportedSymbolTable, reachingDefinitionsAnalyser);
        semanticSenseGenerator.Run();

#if FLAG_PERFORMANCE_TRACKING
        checkpoint2 = sw.ElapsedMilliseconds;
        sw.Stop();
        if (!string.IsNullOrEmpty(FileName))
        {
            Log.Debug("[PERF DETAIL] DataFlow - ReachingDefinitions: {RDA}ms, SemanticSense: {SSG}ms, Total: {Total}ms - File={File}", 
                checkpoint1, checkpoint2 - checkpoint1, checkpoint2, FileName);
        }
        else
        {
            Log.Debug("[PERF DETAIL] DataFlow - ReachingDefinitions: {RDA}ms, SemanticSense: {SSG}ms, Total: {Total}ms", 
                checkpoint1, checkpoint2 - checkpoint1, checkpoint2);
        }
#endif
    }
}