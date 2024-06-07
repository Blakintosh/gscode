using GSCode.Data.Models.Interfaces;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GSCode.NET.LSP; 

public class ScriptCache
{
    private ConcurrentDictionary<Uri, StringBuilder> Scripts { get; } = new();

    public string AddToCache(TextDocumentItem document)
    {
        Uri documentUri = document.Uri;
        Scripts[documentUri] = new(document.Text);

        return document.Text;
    }

    public string UpdateCache(TextDocumentIdentifier document, TextDocumentContentChangeEvent[] changes)
    {
        Uri documentUri = document.Uri;
        StringBuilder cachedVersion = Scripts[documentUri];

        foreach (TextDocumentContentChangeEvent change in changes)
        {
            // If no range is specified then this is an outright replacement of the entire document.
            if(change.Range == null)
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

            if(endLineBase == -1 || endPosition > cachedVersion.Length)
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
        Uri documentUri = document.Uri;
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
    public CachedScriptType Type { get; set; }
    public List<Uri> Dependents { get; } = new();
    public WatchedScript Script { get; set; }
}

public class ScriptManager
{
    private readonly ScriptCache _cache;

    private ConcurrentDictionary<Uri, CachedScript> Scripts { get; } = new();

    public ScriptManager()
    {
        _cache = new();
    }

    public async Task<List<Diagnostic>> AddEditorAsync(TextDocumentItem document)
    {
        string content = _cache.AddToCache(document);
        WatchedEditor script = GetEditor(document);

        await script.ParseAsync(content);

        return await script.GetDiagnosticsAsync();
    }

    public async Task<List<Diagnostic>> UpdateEditorAsync(VersionedTextDocumentIdentifier document, TextDocumentContentChangeEvent[] changes)
    {
        string updatedContent = _cache.UpdateCache(document, changes);
        WatchedEditor script = GetEditor(document);

        await script.ParseAsync(updatedContent);

        return await script.GetDiagnosticsAsync();
    }

    public void RemoveEditor(TextDocumentIdentifier document)
    {
        Uri documentUri = document.Uri;
        Scripts.Remove(documentUri, out _);

        RemoveDependent(documentUri);
    }

    public WatchedEditor? GetParsedEditor(TextDocumentIdentifier document)
    {
        Uri uri = document.Uri;
        if (!Scripts.ContainsKey(uri))
        {
            return null;
        }

        CachedScript script = Scripts[uri];

        return script.Script as WatchedEditor;
    }

    private async Task<IEnumerable<IExportedSymbol>> AddEditorDependenciesAsync(Uri editorUri, List<Uri> dependencyUris)
    {
        List<Task<IEnumerable<IExportedSymbol>>> scriptTasks = new(dependencyUris.Count);

        // Wait for all dependencies to finish processing if they haven't already, then get their exported symbols.
        foreach (Uri dependency in dependencyUris)
        {
            WatchedScript script = AddDependency(editorUri, dependency);

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

    private WatchedScript AddDependency(Uri dependentUri, Uri uri)
    {
        if (!Scripts.TryGetValue(uri, out CachedScript script))
        {
            script = Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Dependency,
                Script = new WatchedDependency(uri)
            };
            script.Script.ParseAsync(File.ReadAllText(uri.LocalPath));
        }

        script.Dependents.Add(dependentUri);

        return script.Script;
    }

    private void RemoveDependent(Uri dependentUri)
    {
        foreach (KeyValuePair<Uri, CachedScript> script in Scripts)
        {
            List<Uri> dependents = script.Value.Dependents;
            if (dependents.Contains(dependentUri))
            {
                dependents.Remove(dependentUri);
            }

            // Housekeeping
            if(dependents.Count == 0 && script.Value.Type == CachedScriptType.Dependency)
            {
                Scripts.Remove(script.Key, out _);
            }
        }
    }

    private WatchedEditor GetEditor(TextDocumentIdentifier document)
    {
        return GetEditorByUri(document.Uri);
    }

    private WatchedEditor GetEditor(TextDocumentItem document)
    {
        return GetEditorByUri(document.Uri);
    }

    private WatchedEditor GetEditorByUri(Uri uri)
    {
        if (!Scripts.ContainsKey(uri))
        {
            Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new WatchedEditor(uri, AddEditorDependenciesAsync)
            };
        }

        CachedScript script = Scripts[uri];

        if(script.Type != CachedScriptType.Editor)
        {
            script = Scripts[uri] = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new WatchedEditor(uri, AddEditorDependenciesAsync)
            };
        }

        return (WatchedEditor)script.Script;
    }
}
