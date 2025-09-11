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

    public List<Uri> Dependencies { get; } = new();

    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _functionLocations = new();
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _classLocations = new();

    private readonly Dictionary<(string Namespace, string Name), string[]> _functionParameters = new();
    private readonly Dictionary<(string Namespace, string Name), string[]> _functionFlags = new();
    private readonly Dictionary<(string Namespace, string Name), string?> _functionDocs = new();

    private static (string Namespace, string Name) NK(string ns, string name)
        => (ns?.ToLowerInvariant() ?? string.Empty, name?.ToLowerInvariant() ?? string.Empty);

    public DefinitionsTable(string currentNamespace)
    {
        CurrentNamespace = currentNamespace;
    }

    internal void AddFunction(ScrFunction function, FunDefnNode node)
    {
        LocalScopedFunctions.Add(new Tuple<ScrFunction, FunDefnNode>(function, node));

        if (!function.IsPrivate)
        {
            ExportedFunctions.Add(function with { Namespace = CurrentNamespace });
        }
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }

    public void AddFunctionLocation(string ns, string name, string filePath, Range range)
    {
        _functionLocations[NK(ns, name)] = (filePath, range);
    }

    public void AddClassLocation(string ns, string name, string filePath, Range range)
    {
        _classLocations[NK(ns, name)] = (filePath, range);
    }

    public void RecordFunctionParameters(string ns, string name, IEnumerable<string> parameterNames)
    {
        _functionParameters[NK(ns, name)] = parameterNames?.Select(p => p?.ToLowerInvariant() ?? string.Empty).ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionParameters(string ns, string name)
    {
        return _functionParameters.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionFlags(string ns, string name, IEnumerable<string> flags)
    {
        _functionFlags[NK(ns, name)] = flags?.Select(f => f?.ToLowerInvariant() ?? string.Empty).ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionFlags(string ns, string name)
    {
        return _functionFlags.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionDoc(string ns, string name, string? doc)
    {
        _functionDocs[NK(ns, name)] = string.IsNullOrWhiteSpace(doc) ? null : doc;
    }

    public string? GetFunctionDoc(string ns, string name)
    {
        return _functionDocs.TryGetValue(NK(ns, name), out var doc) ? doc : null;
    }

    public (string FilePath, Range Range)? GetFunctionLocation(string ns, string name)
    {
        if (ns is not null && _functionLocations.TryGetValue(NK(ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    public (string FilePath, Range Range)? GetClassLocation(string ns, string name)
    {
        if (ns is not null && _classLocations.TryGetValue(NK(ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    public (string FilePath, Range Range)? GetFunctionLocationAnyNamespace(string name)
    {
        string lookup = name?.ToLowerInvariant() ?? string.Empty;
        foreach (var kv in _functionLocations)
        {
            if (string.Equals(kv.Key.Name, lookup, StringComparison.Ordinal))
            {
                return kv.Value;
            }
        }
        return null;
    }

    public (string FilePath, Range Range)? GetClassLocationAnyNamespace(string name)
    {
        string lookup = name?.ToLowerInvariant() ?? string.Empty;
        foreach (var kv in _classLocations)
        {
            if (string.Equals(kv.Key.Name, lookup, StringComparison.Ordinal))
            {
                return kv.Value;
            }
        }
        return null;
    }

    public IEnumerable<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> GetAllFunctionLocations()
    {
        return _functionLocations.ToList();
    }

    public IEnumerable<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> GetAllClassLocations()
    {
        return _classLocations.ToList();
    }

    // New: expose all parameters and docs
    public IEnumerable<KeyValuePair<(string Namespace, string Name), string[]>> GetAllFunctionParameters()
    {
        return _functionParameters.ToList();
    }

    public IEnumerable<KeyValuePair<(string Namespace, string Name), string?>> GetAllFunctionDocs()
    {
        return _functionDocs.ToList();
    }
}