using GSCode.Parser.AST.Nodes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GSCode.Parser.SPA.Logic.Components;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, ASTBranch>> LocalScopedFunctions { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    // TODO: Class definitions (not in this version)

    public List<Uri> Dependencies { get; } = new();

    public DefinitionsTable(string currentNamespace)
    {
        CurrentNamespace = currentNamespace;
    }

    internal void AddFunction(ScrFunction function, ASTBranch branch)
    {
        LocalScopedFunctions.Add(new Tuple<ScrFunction, ASTBranch>(function, branch));
        ExportedFunctions.Add(function with { Namespace = CurrentNamespace });
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }
}
