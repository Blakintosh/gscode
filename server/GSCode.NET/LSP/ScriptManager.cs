using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.SA;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Microsoft.Extensions.Logging;

namespace GSCode.NET.LSP;

public class ScriptCache
{
    private ConcurrentDictionary<DocumentUri, StringBuilder> Scripts { get; } = new();

    public string AddToCache(TextDocumentItem document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts[documentUri] = new(document.Text);

        return document.Text;
    }

    public string UpdateCache(TextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes)
    {
        DocumentUri documentUri = document.Uri;
        StringBuilder cachedVersion = Scripts[documentUri];

        foreach (TextDocumentContentChangeEvent change in changes)
        {
            // If no range is specified then this is an outright replacement of the entire document.
            if (change.Range == null)
            {
                cachedVersion = new(change.Text);
                continue;
            }

            Position start = change.Range.Start;
            Position end = change.Range.End;

            // Otherwise modify the buffer
            string cachedString = cachedVersion.ToString();
            int startPosition = GetBaseCharOfLine(cachedString, start.Line) + start.Character;
            int endLineBase = GetBaseCharOfLine(cachedString, end.Line);
            int endPosition = endLineBase + end.Character;

            if (endLineBase == -1 || endPosition > cachedVersion.Length)
            {
                cachedVersion.Remove(startPosition, cachedVersion.Length - startPosition);
                cachedVersion.Append(change.Text);
                continue;
            }

            cachedVersion.Remove(startPosition, endPosition - startPosition);
            cachedVersion.Insert(startPosition, change.Text);
        }

        return cachedVersion.ToString();
    }

    private int GetBaseCharOfLine(string content, int line)
    {
        int pos = -1;
        do
        {
            pos = content.IndexOf(Environment.NewLine, pos + 1);
        } while (line-- > 0 && pos != -1);
        return pos;
    }

    public void RemoveFromCache(TextDocumentIdentifier document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts.Remove(documentUri, out StringBuilder? value);
    }
}

public enum CachedScriptType
{
    Editor,
    Dependency
}

public class CachedScript
{
    public CachedScriptType Type { get; init; }
    public HashSet<DocumentUri> Dependents { get; } = [];
    public required Script Script { get; init; }
}

public readonly record struct LoadedScript(DocumentUri Uri, Script Script);

public class ScriptManager
{
    private readonly ScriptCache _cache;
    private readonly ILogger<ScriptManager> _logger;

    private ConcurrentDictionary<DocumentUri, CachedScript> Scripts { get; } = new();

    public ScriptManager(ILogger<ScriptManager> logger)
    {
        _cache = new();
        _logger = logger;
    }

    public async Task<IEnumerable<Diagnostic>> AddEditorAsync(TextDocumentItem document, CancellationToken cancellationToken = default)
    {
        string content = _cache.AddToCache(document);
        Script script = GetEditor(document);

        return await ProcessEditorAsync(document.Uri.ToUri(), script, content, cancellationToken);
    }

    public async Task<IEnumerable<Diagnostic>> UpdateEditorAsync(OptionalVersionedTextDocumentIdentifier document, IEnumerable<TextDocumentContentChangeEvent> changes, CancellationToken cancellationToken = default)
    {
        string updatedContent = _cache.UpdateCache(document, changes);
        Script script = GetEditor(document);

        return await ProcessEditorAsync(document.Uri.ToUri(), script, updatedContent, cancellationToken);
    }

    private async Task<IEnumerable<Diagnostic>> ProcessEditorAsync(Uri documentUri, Script script, string content, CancellationToken cancellationToken = default)
    {
        await script.ParseAsync(content);

        List<Task> dependencyTasks = new();

        // Now, get their dependencies and parse them.
        foreach (Uri dependency in script.Dependencies)
        {
            dependencyTasks.Add(AddDependencyAsync(documentUri, dependency, script.LanguageId));
        }

        await Task.WhenAll(dependencyTasks);

        // Using this, we can now get the exported symbols for the script.
        List<IExportedSymbol> exportedSymbols = new();

        // TODO: find a cleaner way to do this.
        foreach (Uri dependency in script.Dependencies)
        {
            if (Scripts.TryGetValue(dependency, out CachedScript? cachedScript))
            {
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Merge dependency function/class locations into this script's DefinitionsTable so qualified lookups work.
        // This allows go-to-definition for namespace::function where the namespace is defined in a #using file.
        if (script.DefinitionsTable is not null)
        {
            foreach (Uri dependency in script.Dependencies)
            {
                if (!Scripts.TryGetValue(dependency, out CachedScript? depScript))
                    continue;

                DefinitionsTable? depTable = depScript.Script.DefinitionsTable;
                if (depTable is null)
                    continue;

                // Import all function locations
                foreach (var kv in depTable.GetAllFunctionLocations())
                {
                    var key = kv.Key;
                    var val = kv.Value;
                    script.DefinitionsTable.AddFunctionLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }

                // Import all class locations
                foreach (var kv in depTable.GetAllClassLocations())
                {
                    var key = kv.Key;
                    var val = kv.Value;
                    script.DefinitionsTable.AddClassLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }

                // Import function parameters
                foreach (var kv in depTable.GetAllFunctionParameters())
                {
                    var key = kv.Key;
                    var parms = kv.Value;
                    script.DefinitionsTable.RecordFunctionParameters(key.Namespace, key.Name, parms);
                }

                // Import function docs
                foreach (var kv in depTable.GetAllFunctionDocs())
                {
                    var key = kv.Key;
                    var doc = kv.Value;
                    script.DefinitionsTable.RecordFunctionDoc(key.Namespace, key.Name, doc);
                }
            }
        }

        // Finally, analyse the script.
        await script.AnalyseAsync(exportedSymbols, cancellationToken);

        // await script.GetHoverAsync(new Position(13, 15), cancellationToken);

        return await script.GetDiagnosticsAsync(cancellationToken);
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        DocumentUri documentUri = document.Uri;
        Scripts.Remove(documentUri, out _);

        RemoveDependent(documentUri);
    }

    public Script? GetParsedEditor(TextDocumentIdentifier document)
    {
        DocumentUri uri = document.Uri;
        if (!Scripts.ContainsKey(uri))
        {
            return null;
        }

        CachedScript script = Scripts[uri];

        return script.Script;
    }

    /// <summary>
    /// Try to find a symbol (function or class) in any cached script. If ns is provided, search that namespace first.
    /// Returns a Location or null.
    /// </summary>
    public Location? FindSymbolLocation(string? ns, string name)
    {
        foreach (KeyValuePair<DocumentUri, CachedScript> kvp in Scripts)
        {
            CachedScript cached = kvp.Value;
            if (cached.Script.DefinitionsTable is null)
                continue;

            // If namespace provided, try that first for this table.
            if (ns is not null)
            {
                var funcLoc = cached.Script.DefinitionsTable.GetFunctionLocation(ns, name);
                if (funcLoc is not null && File.Exists(funcLoc.Value.FilePath))
                {
                    return new Location() { Uri = new Uri(funcLoc.Value.FilePath), Range = funcLoc.Value.Range };
                }

                var classLoc = cached.Script.DefinitionsTable.GetClassLocation(ns, name);
                if (classLoc is not null && File.Exists(classLoc.Value.FilePath))
                {
                    return new Location() { Uri = new Uri(classLoc.Value.FilePath), Range = classLoc.Value.Range };
                }
            }

            // Try any namespace in this table
            var funcAny = cached.Script.DefinitionsTable.GetFunctionLocationAnyNamespace(name);
            if (funcAny is not null && File.Exists(funcAny.Value.FilePath))
            {
                return new Location() { Uri = new Uri(funcAny.Value.FilePath), Range = funcAny.Value.Range };
            }

            var classAny = cached.Script.DefinitionsTable.GetClassLocationAnyNamespace(name);
            if (classAny is not null && File.Exists(classAny.Value.FilePath))
            {
                return new Location() { Uri = new Uri(classAny.Value.FilePath), Range = classAny.Value.Range };
            }
        }

        return null;
    }

#if PREVIEW
    private async Task<IEnumerable<IExportedSymbol>> AddEditorDependenciesAsync(Uri editorUri, List<Uri> dependencyUris)
    {
        List<Task<IEnumerable<IExportedSymbol>>> scriptTasks = new(dependencyUris.Count);

        // Wait for all dependencies to finish processing if they haven't already, then get their exported symbols.
        foreach (Uri dependency in dependencyUris)
        {
            Script script = AddDependency(editorUri, dependency);

            scriptTasks.Add(script.IssueExportedSymbolsAsync());
        }

        // Wait for all tasks to complete and collect their results
        IEnumerable<IExportedSymbol>[] results = await Task.WhenAll(scriptTasks);

        // Merge all exported symbols into a single IEnumerable
        IEnumerable<IExportedSymbol> merged;
        if (results.Length > 0)
        {
            merged = results.Aggregate((acc, e) => acc.Union(e));
        }
        else
        {
            merged = Array.Empty<IExportedSymbol>();
        }

        return merged;
    }
#endif

    private async Task<Script> AddDependencyAsync(Uri dependentUri, Uri uri, string languageId)
    {
        if (!Scripts.TryGetValue(uri, out CachedScript? script))
        {
            script = Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Dependency,
                Script = new Script(uri, languageId)
            };
            await script.Script.ParseAsync(File.ReadAllText(uri.LocalPath));
        }

        script.Dependents.Add(dependentUri);

        return script.Script;
    }

    private void RemoveDependent(DocumentUri dependentUri)
    {
        foreach (KeyValuePair<DocumentUri, CachedScript> script in Scripts)
        {
            HashSet<DocumentUri> dependents = script.Value.Dependents;
            if (dependents.Contains(dependentUri))
            {
                dependents.Remove(dependentUri);
            }

            // Housekeeping
            if (dependents.Count == 0 && script.Value.Type == CachedScriptType.Dependency)
            {
                Scripts.Remove(script.Key, out _);
            }
        }
    }

    private Script GetEditor(TextDocumentIdentifier document)
    {
        return GetEditorByUri(document.Uri);
    }

    private Script GetEditor(TextDocumentItem document)
    {
        return GetEditorByUri(document.Uri, document.LanguageId);
    }

    private Script GetEditorByUri(DocumentUri uri, string? languageId = null)
    {
        if (!Scripts.ContainsKey(uri))
        {
            Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new Script(uri, languageId ?? "gsc")
                // uri, add editor dependencies async
            };
        }

        CachedScript script = Scripts[uri];

        if (script.Type != CachedScriptType.Editor)
        {
            script = Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new Script(uri, languageId ?? "gsc")
            };
        }

        return script.Script;
    }

    public IEnumerable<LoadedScript> GetLoadedScripts()
    {
        foreach (var kv in Scripts)
        {
            yield return new LoadedScript(kv.Key, kv.Value.Script);
        }
    }
}
