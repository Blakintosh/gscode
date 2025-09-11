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
using System.IO; // added
using System.Linq; // added
using System.Threading; // added
using System.Diagnostics; // added
using OmniSharp.Extensions.LanguageServer.Protocol.Server; // added
using OmniSharp.Extensions.LanguageServer.Protocol.Document; // added for PublishDiagnostics extension

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
    // Thread-safe set of dependents
    public ConcurrentDictionary<DocumentUri, byte> Dependents { get; } = new();
    public required Script Script { get; init; }
}

public readonly record struct LoadedScript(DocumentUri Uri, Script Script);

public class ScriptManager
{
    private readonly ScriptCache _cache;
    private readonly ILogger<ScriptManager> _logger;
    private readonly ILanguageServerFacade? _facade; // added

    private ConcurrentDictionary<DocumentUri, CachedScript> Scripts { get; } = new();

    // Ensure only one parse per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _parseLocks = new();
    // Ensure only one analysis/merge per script at a time
    private readonly ConcurrentDictionary<DocumentUri, SemaphoreSlim> _analysisLocks = new();

    public ScriptManager(ILogger<ScriptManager> logger, ILanguageServerFacade? facade = null)
    {
        _cache = new();
        _logger = logger;
        _facade = facade; // added
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

        // Build exported symbols
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dependency in script.Dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (Scripts.TryGetValue(depDoc, out CachedScript? cachedScript))
            {
                await EnsureParsedAsync(depDoc, cachedScript.Script, script.LanguageId, cancellationToken);
                exportedSymbols.AddRange(await cachedScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Snapshot dependency locations while locking each dependency individually
        var mergeFuncLocs = new List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>>();
        var mergeClassLocs = new List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>>();
        foreach (Uri dependency in script.Dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (!Scripts.TryGetValue(depDoc, out CachedScript? depScript)) continue;
            await WithAnalysisLockAsync(depDoc, async () =>
            {
                var depTable = depScript.Script.DefinitionsTable;
                if (depTable is null) return;
                mergeFuncLocs.AddRange(depTable.GetAllFunctionLocations());
                mergeClassLocs.AddRange(depTable.GetAllClassLocations());
                await Task.CompletedTask;
            });
        }

        // Merge + analyse under this script's analysis lock
        var thisDoc = DocumentUri.From(documentUri);
        await WithAnalysisLockAsync(thisDoc, async () =>
        {
            if (script.DefinitionsTable is not null)
            {
                foreach (var kv in mergeFuncLocs)
                {
                    var key = kv.Key; var val = kv.Value;
                    script.DefinitionsTable.AddFunctionLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }
                foreach (var kv in mergeClassLocs)
                {
                    var key = kv.Key; var val = kv.Value;
                    script.DefinitionsTable.AddClassLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }
            }
            await script.AnalyseAsync(exportedSymbols, cancellationToken);
        });

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

    private async Task EnsureParsedAsync(DocumentUri docUri, Script script, string? languageId, CancellationToken cancellationToken)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!script.Parsed)
            {
                // Read file from disk and parse
                string path = docUri.ToUri().LocalPath;
                string content = await File.ReadAllTextAsync(path, cancellationToken);
                await script.ParseAsync(content);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WithAnalysisLockAsync(DocumentUri docUri, Func<Task> action)
    {
        var gate = _analysisLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Script> AddDependencyAsync(Uri dependentUri, Uri uri, string languageId)
    {
        var docUri = DocumentUri.From(uri);
        var cached = Scripts.GetOrAdd(docUri, key => new CachedScript
        {
            Type = CachedScriptType.Dependency,
            Script = new Script(key, languageId)
        });

        await EnsureParsedAsync(docUri, cached.Script, languageId, CancellationToken.None);

        cached.Dependents.TryAdd(DocumentUri.From(dependentUri), 0);

        return cached.Script;
    }

    private void RemoveDependent(DocumentUri dependentUri)
    {
        foreach (KeyValuePair<DocumentUri, CachedScript> script in Scripts)
        {
            var dependents = script.Value.Dependents;
            if (dependents.TryRemove(dependentUri, out _))
            {
                // Housekeeping
                if (dependents.IsEmpty && script.Value.Type == CachedScriptType.Dependency)
                {
                    Scripts.Remove(script.Key, out _);
                }
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

    // =========================
    // Recursive workspace indexing
    // =========================

    public async Task IndexWorkspaceAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                _logger.LogWarning("IndexWorkspace skipped: directory not found: {Root}", rootDirectory);
                return;
            }

            // Collect files first for count and deterministic iteration
            var filesList = Directory
                .EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.LogInformation("Indexing workspace under {Root}", rootDirectory);
            _logger.LogInformation("Indexing started: {Count} files", filesList.Count);
            var swAll = Stopwatch.StartNew();

            int maxDegree = Math.Max(1, Environment.ProcessorCount - 1);
            using SemaphoreSlim gate = new(maxDegree, maxDegree);
            List<Task> tasks = new();

            foreach (string file in filesList)
            {
                await gate.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(async () =>
                {
                    var fileSw = Stopwatch.StartNew();
                    string rel = Path.GetRelativePath(rootDirectory, file);
                    try
                    {
                        _logger.LogInformation("Indexing {File}", rel);
                        await IndexFileAsync(file, cancellationToken);
                        fileSw.Stop();
                        _logger.LogInformation("Indexed {File} in {ElapsedMs} ms", rel, fileSw.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to index {File}", file);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
            swAll.Stop();
            _logger.LogInformation("Indexing completed in {ElapsedMs} ms for {Count} files", swAll.ElapsedMilliseconds, filesList.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Indexing cancelled");
        }
    }

    private async Task IndexFileAsync(string filePath, CancellationToken cancellationToken)
    {
        string ext = Path.GetExtension(filePath);
        string languageId = string.Equals(ext, ".csc", StringComparison.OrdinalIgnoreCase) ? "csc" : "gsc";

        DocumentUri docUri = DocumentUri.FromFileSystemPath(filePath);

        var cached = Scripts.GetOrAdd(docUri, key => new CachedScript
        {
            Type = CachedScriptType.Dependency,
            Script = new Script(key, languageId)
        });

        await EnsureParsedAsync(docUri, cached.Script, languageId, cancellationToken);

        // Parse and include dependencies
        foreach (Uri dep in cached.Script.Dependencies)
        {
            await AddDependencyAsync(docUri.ToUri(), dep, languageId);
        }

        // Ensure dependencies are parsed before exporting/merging
        foreach (Uri dep in cached.Script.Dependencies)
        {
            var depDoc = DocumentUri.From(dep);
            if (Scripts.TryGetValue(depDoc, out CachedScript? depScript))
            {
                await EnsureParsedAsync(depDoc, depScript.Script, languageId, cancellationToken);
            }
        }

        // Build exported symbols
        List<IExportedSymbol> exportedSymbols = new();
        foreach (Uri dep in cached.Script.Dependencies)
        {
            var depDoc = DocumentUri.From(dep);
            if (Scripts.TryGetValue(depDoc, out CachedScript? depScript))
            {
                exportedSymbols.AddRange(await depScript.Script.IssueExportedSymbolsAsync(cancellationToken));
            }
        }

        // Snapshot dependency locations under dep locks
        var mergeFuncLocs = new List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>>();
        var mergeClassLocs = new List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>>();
        foreach (Uri dep in cached.Script.Dependencies)
        {
            var depDoc = DocumentUri.From(dep);
            if (!Scripts.TryGetValue(depDoc, out CachedScript? depScript)) continue;
            await WithAnalysisLockAsync(depDoc, async () =>
            {
                var depTable = depScript.Script.DefinitionsTable;
                if (depTable is null) return;
                mergeFuncLocs.AddRange(depTable.GetAllFunctionLocations());
                mergeClassLocs.AddRange(depTable.GetAllClassLocations());
                await Task.CompletedTask;
            });
        }

        // Merge + analyse under this script's analysis lock
        await WithAnalysisLockAsync(docUri, async () =>
        {
            if (cached.Script.DefinitionsTable is not null)
            {
                foreach (var kv in mergeFuncLocs)
                {
                    var key = kv.Key; var val = kv.Value;
                    cached.Script.DefinitionsTable.AddFunctionLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }
                foreach (var kv in mergeClassLocs)
                {
                    var key = kv.Key; var val = kv.Value;
                    cached.Script.DefinitionsTable.AddClassLocation(key.Namespace, key.Name, val.FilePath, val.Range);
                }
            }
            await cached.Script.AnalyseAsync(exportedSymbols, cancellationToken);

            // Publish diagnostics for indexed file (if LSP facade is available)
            await PublishDiagnosticsAsync(docUri, cached.Script, cancellationToken: cancellationToken);
        });
    }

    private async Task PublishDiagnosticsAsync(DocumentUri uri, Script script, int? version = null, CancellationToken cancellationToken = default)
    {
        if (_facade is null) return;
        var diags = await script.GetDiagnosticsAsync(cancellationToken);
        try
        {
            _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = uri,
                Version = version,
                Diagnostics = new Container<Diagnostic>(diags)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish diagnostics for {Uri}", uri.GetFileSystemPath());
        }
    }
}
