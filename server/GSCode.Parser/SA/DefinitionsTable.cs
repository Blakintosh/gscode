using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.AST;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, FunDefnNode>> LocalScopedFunctions { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    // TODO: Class definitions (not in this version)

    public List<Uri> Dependencies { get; } = new();

    // Store locations for functions and classes keyed by (namespace, name)
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _functionLocations = new();
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _classLocations = new();

    // Store function parameter names for outline/signature display
    private readonly Dictionary<(string Namespace, string Name), string[]> _functionParameters = new();

    // Store function flags (e.g. private, autoexec)
    private readonly Dictionary<(string Namespace, string Name), string[]> _functionFlags = new();

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

    // APIs to record and query locations
    public void AddFunctionLocation(string ns, string name, string filePath, Range range)
    {
        _functionLocations[(ns, name)] = (filePath, range);
    }

    public void AddClassLocation(string ns, string name, string filePath, Range range)
    {
        _classLocations[(ns, name)] = (filePath, range);
    }

    // Record function parameter names for signature display
    public void RecordFunctionParameters(string ns, string name, IEnumerable<string> parameterNames)
    {
        _functionParameters[(ns, name)] = parameterNames?.ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionParameters(string ns, string name)
    {
        return _functionParameters.TryGetValue((ns, name), out var list) ? list : null;
    }

    // Record function flags for outline/signature display
    public void RecordFunctionFlags(string ns, string name, IEnumerable<string> flags)
    {
        _functionFlags[(ns, name)] = flags?.ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionFlags(string ns, string name)
    {
        return _functionFlags.TryGetValue((ns, name), out var list) ? list : null;
    }

    public (string FilePath, Range Range)? GetFunctionLocation(string ns, string name)
    {
        if (ns is not null && _functionLocations.TryGetValue((ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    public (string FilePath, Range Range)? GetClassLocation(string ns, string name)
    {
        if (ns is not null && _classLocations.TryGetValue((ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    // Helper to try all namespaces if a namespace wasn't provided
    public (string FilePath, Range Range)? GetFunctionLocationAnyNamespace(string name)
    {
        foreach (var kv in _functionLocations)
        {
            if (kv.Key.Name == name)
            {
                return kv.Value;
            }
        }
        return null;
    }

    public (string FilePath, Range Range)? GetClassLocationAnyNamespace(string name)
    {
        foreach (var kv in _classLocations)
        {
            if (kv.Key.Name == name)
            {
                return kv.Value;
            }
        }
        return null;
    }

    // Expose all stored locations so other scripts can import them
    public IEnumerable<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> GetAllFunctionLocations()
    {
        return _functionLocations.ToList();
    }

    public IEnumerable<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> GetAllClassLocations()
    {
        return _classLocations.ToList();
    }
}