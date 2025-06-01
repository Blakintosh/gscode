using GSCode.Data.Models.Interfaces;
using GSCode.Parser;
using GSCode.Parser.Data;
using Serilog;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Threading.Tasks;
using GSCode.Parser.SPA;
using GSCode.Data.Models;

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

        // Add built-in functions from the API
        ScriptAnalyserData analyserData = new(script.LanguageId);
        List<ScrFunction> apiFunctions = analyserData.GetApiFunctions();
        foreach (ScrFunction apiFunction in apiFunctions)
        {
            exportedSymbols.Add(apiFunction);
        }

        // TODO: find a cleaner way to do this.
        foreach (Uri dependency in script.Dependencies)
        {
            if (Scripts.TryGetValue(dependency, out CachedScript? cachedScript))
            {
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
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
}
