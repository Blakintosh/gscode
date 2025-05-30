﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, FunDefnNode>> LocalScopedFunctions { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    // TODO: Class definitions (not in this version)

    public List<Uri> Dependencies { get; } = new();

    public DefinitionsTable(string currentNamespace)
    {
        CurrentNamespace = currentNamespace;
    }

    internal void AddFunction(ScrFunction function, FunDefnNode node)
    {
        LocalScopedFunctions.Add(new Tuple<ScrFunction, FunDefnNode>(function, node));

        // Only add to exported functions if it's not private.
        if (!function.IsPrivate)
        {
            ExportedFunctions.Add(function with { Namespace = CurrentNamespace });
        }
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }
}