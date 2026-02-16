using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GSCode.Parser.AST;
using GSCode.Parser.Data;
using GSCode.Parser.Lexical;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Parser.SA;

public class DefinitionsTable
{
    public string CurrentNamespace { get; set; }

    internal List<Tuple<ScrFunction, FunDefnNode>> LocalScopedFunctions { get; } = new();
    internal List<Tuple<ScrClass, ClassDefnNode>> LocalScopedClasses { get; } = new();
    public List<ScrFunction> ExportedFunctions { get; } = new();
    public List<ScrClass> ExportedClasses { get; } = new();
    public Dictionary<string, IExportedSymbol> InternalSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IExportedSymbol> ExportedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> Dependencies { get; } = new();

    // Local dictionaries for symbols defined in THIS file only (used for merging/exporting)
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _functionLocations = new();
    private readonly Dictionary<(string Namespace, string Name), (string FilePath, Range Range)> _classLocations = new();

    private readonly Dictionary<(string Namespace, string Name), string[]> _functionParameters = new();
    private readonly Dictionary<(string Namespace, string Name), string[]> _functionFlags = new();
    private readonly Dictionary<(string Namespace, string Name), string?> _functionDocs = new();

    /// <summary>
    /// Optional global symbol registry for workspace-wide O(1) lookups.
    /// When set, lookup methods will query the global registry first before falling back to local dictionaries.
    /// </summary>
    private readonly ISymbolLocationProvider? _globalProvider;

    private static (string Namespace, string Name) NK(string ns, string name)
        => (StringPool.Intern(ns?.ToLowerInvariant() ?? string.Empty), StringPool.Intern(name?.ToLowerInvariant() ?? string.Empty));

    public DefinitionsTable(string currentNamespace, ISymbolLocationProvider? globalProvider = null)
    {
        CurrentNamespace = StringPool.Intern(currentNamespace);
        _globalProvider = globalProvider;
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

    internal void AddClass(ScrClass scrClass, ClassDefnNode node)
    {
        LocalScopedClasses.Add(new Tuple<ScrClass, ClassDefnNode>(scrClass, node));

        // Add to internal symbols for within-file references
        InternalSymbols.Add(scrClass.Name, scrClass);
        InternalSymbols.Add($"{CurrentNamespace}::{scrClass.Name}", scrClass);

        // Always export classes (GSC doesn't have private classes)
        ExportedClasses.Add(scrClass);
        ExportedSymbols.Add(scrClass.Name, scrClass);
    }

    public void AddDependency(string scriptPath)
    {
        Dependencies.Add(new Uri(scriptPath));
    }

    public void AddFunctionLocation(string ns, string name, string filePath, Range range)
    {
        _functionLocations[NK(ns, name)] = (StringPool.Intern(filePath), range);
    }

    public void AddClassLocation(string ns, string name, string filePath, Range range)
    {
        _classLocations[NK(ns, name)] = (StringPool.Intern(filePath), range);
    }

    public void RecordFunctionParameters(string ns, string name, IEnumerable<string> parameterNames)
    {
        _functionParameters[NK(ns, name)] = parameterNames?.Select(p => StringPool.Intern(p?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionParameters(string ns, string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionParameters(ns, name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary
        return _functionParameters.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionFlags(string ns, string name, IEnumerable<string> flags)
    {
        _functionFlags[NK(ns, name)] = flags?.Select(f => StringPool.Intern(f?.ToLowerInvariant() ?? string.Empty)).ToArray() ?? Array.Empty<string>();
    }

    public string[]? GetFunctionFlags(string ns, string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionFlags(ns, name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary
        return _functionFlags.TryGetValue(NK(ns, name), out var list) ? list : null;
    }

    public void RecordFunctionDoc(string ns, string name, string? doc)
    {
        _functionDocs[NK(ns, name)] = string.IsNullOrWhiteSpace(doc) ? null : doc;
    }

    public string? GetFunctionDoc(string ns, string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.GetFunctionDoc(ns, name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary
        return _functionDocs.TryGetValue(NK(ns, name), out var doc) ? doc : null;
    }

    public (string FilePath, Range Range)? GetFunctionLocation(string ns, string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindFunctionLocation(ns, name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary
        if (ns is not null && _functionLocations.TryGetValue(NK(ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    public (string FilePath, Range Range)? GetClassLocation(string ns, string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindClassLocation(ns, name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary
        if (ns is not null && _classLocations.TryGetValue(NK(ns, name), out var loc))
        {
            return loc;
        }
        return null;
    }

    public (string FilePath, Range Range)? GetFunctionLocationAnyNamespace(string name)
    {
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindFunctionLocationAnyNamespace(name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary with O(n) search
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
        // Try global provider first for O(1) lookup
        if (_globalProvider is not null)
        {
            var result = _globalProvider.FindClassLocationAnyNamespace(name);
            if (result is not null)
                return result;
        }
        // Fall back to local dictionary with O(n) search
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