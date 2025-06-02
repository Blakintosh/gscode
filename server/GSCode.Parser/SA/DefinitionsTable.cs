
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, FunDefnNode>> LocalScopedFunctions { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    public Dictionary<string, IExportedSymbol> InternalSymbols { get; } = new();
    public Dictionary<string, IExportedSymbol> ExportedSymbols { get; } = new();
    // TODO: Class definitions (not in this version)

    public List<Uri> Dependencies { get; } = new();

    public DefinitionsTable(string currentNamespace)
    {
        CurrentNamespace = currentNamespace;
    }

    internal void AddFunction(ScrFunction function, FunDefnNode node)
    {
        LocalScopedFunctions.Add(new Tuple<ScrFunction, FunDefnNode>(function, node));

        ScrFunction internalFunction = function with { Namespace = CurrentNamespace, Implicit = true };
        InternalSymbols.Add(function.Name, internalFunction);
        InternalSymbols.Add($"{CurrentNamespace}::{function.Name}", internalFunction);

        // Only add to exported functions if it's not private.
        if (!function.Private)
        {
            ScrFunction exportedFunction = function with { Namespace = CurrentNamespace };
            ExportedFunctions.Add(exportedFunction);
            ExportedSymbols.Add(exportedFunction.Name, exportedFunction);
        }
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }
}