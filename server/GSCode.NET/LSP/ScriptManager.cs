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
using GSCode.NET.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

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

    // Expose current text for an editor doc (used to reparse dependents without losing unsaved edits)
    public bool TryGetText(DocumentUri uri, out string text)
    {
        if (Scripts.TryGetValue(uri, out var sb))
        {
            text = sb.ToString();
            return true;
        }
        text = string.Empty;
        return false;
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

internal sealed class DependencySnapshot
{
    public List<IExportedSymbol> ExportedSymbols { get; } = new();
    public List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> FunctionLocations { get; } = new();
    public List<KeyValuePair<(string Namespace, string Name), (string FilePath, Range Range)>> ClassLocations { get; } = new();
    public List<KeyValuePair<(string Namespace, string Name), string[]>> FunctionParameters { get; } = new();
    public List<KeyValuePair<(string Namespace, string Name), string?>> FunctionDocs { get; } = new();
    public List<KeyValuePair<(string Namespace, string Name), bool>> FunctionVarargs { get; } = new();
}

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

        // Collect snapshot info and exported symbols from deps
        var snapshot = await SnapshotDependenciesAsync(script.Dependencies, script.LanguageId, cancellationToken);

        // Merge + analyse under this script's analysis lock
        var thisDoc = DocumentUri.From(documentUri);
        await WithAnalysisLockAsync(thisDoc, async () =>
        {
            ApplyDependencySnapshot(script, snapshot);
            await script.AnalyseAsync(snapshot.ExportedSymbols, cancellationToken);
        });

        return await script.GetDiagnosticsAsync(cancellationToken);
    }

    // Re-analyse current document on save; then refresh dependents so external references update
    public async Task<IEnumerable<Diagnostic>> RefreshEditorOnSaveAsync(TextDocumentIdentifier document, CancellationToken cancellationToken = default)
    {
        var script = GetEditor(document);
        string path = document.Uri.ToUri().LocalPath;

        // Read saved content from disk
        string content = await File.ReadAllTextAsync(path, cancellationToken);

        // Full re-parse + re-analysis (resets DefinitionsTable and references)
        var diagnostics = await ProcessEditorAsync(document.Uri.ToUri(), script, content, cancellationToken);

        // Publish diagnostics for the saved doc
        await PublishDiagnosticsAsync(document.Uri, script, document is OptionalVersionedTextDocumentIdentifier v ? v.Version : null, cancellationToken);

        // Refresh all dependents of this document to update their view of our DefinitionsTable
        await RefreshDependentsAsync(document.Uri, cancellationToken);

        return diagnostics;
    }

    // Parse from latest available source (editor buffer if open, otherwise disk)
    private async Task ReparseFromLatestAsync(DocumentUri docUri, Script script, CancellationToken cancellationToken)
    {
        if (Scripts.TryGetValue(docUri, out var cached) &&
            cached.Type == CachedScriptType.Editor &&
            _cache.TryGetText(docUri, out var text) &&
            !string.IsNullOrEmpty(text))
        {
            await script.ParseAsync(text);
        }
        else
        {
            await ParseFromDiskAsync(docUri, script, cancellationToken);
        }
    }

    // In RefreshDependentsAsync: reparse each dependent from latest source, not just disk
    private async Task RefreshDependentsAsync(DocumentUri savedDoc, CancellationToken cancellationToken)
    {
        if (!Scripts.TryGetValue(savedDoc, out var savedCached))
            return;

        foreach (var dep in savedCached.Dependents.Keys)
        {
            if (!Scripts.TryGetValue(dep, out var dependentCached))
                continue;

            var dependentScript = dependentCached.Script;

            // Re-parse to rebuild macro outlines from #insert expansions
            await ReparseFromLatestAsync(dep, dependentScript, cancellationToken);

            var snapshot = await SnapshotDependenciesAsync(dependentScript.Dependencies, dependentScript.LanguageId, cancellationToken);

            await WithAnalysisLockAsync(dep, async () =>
            {
                ApplyDependencySnapshot(dependentScript, snapshot);
                await dependentScript.AnalyseAsync(snapshot.ExportedSymbols, cancellationToken);
                await PublishDiagnosticsAsync(dep, dependentScript, cancellationToken: cancellationToken);
            });
        }
    }

    // In RemoveScriptAndRefreshDependentsAsync: same change to ensure clean macro state
    private async Task RemoveScriptAndRefreshDependentsAsync(DocumentUri docUri, CancellationToken cancellationToken)
    {
        if (!Scripts.Remove(docUri, out CachedScript? removed))
            return;

        foreach (var dep in removed.Dependents.Keys)
        {
            if (!Scripts.TryGetValue(dep, out var dependentCached))
                continue;

            var dependentScript = dependentCached.Script;

            // Re-parse dependent (buffer if open) to drop stale macros and rebuild outlines
            await ReparseFromLatestAsync(dep, dependentScript, cancellationToken);

            var snapshot = await SnapshotDependenciesAsync(dependentScript.Dependencies, dependentScript.LanguageId, cancellationToken);

            await WithAnalysisLockAsync(dep, async () =>
            {
                ApplyDependencySnapshot(dependentScript, snapshot);
                await dependentScript.AnalyseAsync(snapshot.ExportedSymbols, cancellationToken);
                await PublishDiagnosticsAsync(dep, dependentScript, cancellationToken: cancellationToken);
            });
        }
    }

    // Entry point used by workspace/didChangeWatchedFiles
    public async Task HandleWatchedFilesChangedAsync(IEnumerable<FileEvent> changes, CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var docUri = change.Uri;

            switch (change.Type)
            {
                case FileChangeType.Created:
                case FileChangeType.Changed:
                    {
                        // If this file is open in the editor, let textDocument/didChange|didSave handle it
                        if (Scripts.TryGetValue(docUri, out var cached) && cached.Type == CachedScriptType.Editor)
                        {
                            continue;
                        }

                        // Index (parse + analyse) and publish diagnostics
                        try
                        {
                            string path = docUri.ToUri().LocalPath;
                            if (File.Exists(path))
                            {
                                await IndexFileAsync(path, cancellationToken);
                                // Refresh dependents that reference this file
                                await RefreshDependentsAsync(docUri, cancellationToken);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to index changed file {Path}", docUri.GetFileSystemPath());
                        }
                        break;
                    }
                case FileChangeType.Deleted:
                    {
                        try
                        {
                            await RemoveScriptAndRefreshDependentsAsync(docUri, cancellationToken);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to remove deleted file {Path}", docUri.GetFileSystemPath());
                        }
                        break;
                    }
            }
        }
    }

    // =====================
    // Additional public APIs expected by handlers
    // =====================

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

    private async Task ParseFromDiskAsync(DocumentUri docUri, Script script, CancellationToken cancellationToken)
    {
        var gate = _parseLocks.GetOrAdd(docUri, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            string path = docUri.ToUri().LocalPath;
            string content = await File.ReadAllTextAsync(path, cancellationToken);
            await script.ParseAsync(content);
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

        CachedScript cached = Scripts[uri];

        if (cached.Type != CachedScriptType.Editor)
        {
            // Preserve existing dependents when promoting to editor
            var dependents = cached.Dependents;
            var newCached = new CachedScript()
            {
                Type = CachedScriptType.Editor,
                Script = new Script(uri, languageId ?? "gsc")
            };
            foreach (var kv in dependents)
            {
                newCached.Dependents.TryAdd(kv.Key, 0);
            }
            Scripts[uri] = newCached;
            cached = newCached;
        }

        return cached.Script;
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

            var files = Directory
                .EnumerateFiles(rootDirectory, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".csc", StringComparison.OrdinalIgnoreCase));

            _logger.LogDebug("Indexing workspace under {Root}", rootDirectory);
            int count = files.Count(); // enumerates, but does not materialize the list
            _logger.LogInformation("Indexing started: {Count} files", count);
            var swAll = Stopwatch.StartNew();

            int maxDegree = Math.Max(1, Environment.ProcessorCount - 1);
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(files, po, async (file, ct) =>
            {
                var fileSw = Stopwatch.StartNew();
                string rel = Path.GetRelativePath(rootDirectory, file);
                _logger.LogDebug("Indexing {File}", rel);
                try
                {
                    await IndexFileAsync(file, ct);
                    fileSw.Stop();
                    _logger.LogDebug("Indexed {File} in {ElapsedMs} ms", rel, fileSw.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to index {File}", file);
                }
            });

            swAll.Stop();
            _logger.LogDebug("Indexing finished in {ElapsedMs} ms", swAll.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { }
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

        // Always parse fresh from disk for indexed files
        await ParseFromDiskAsync(docUri, cached.Script, cancellationToken);

        // Parse and include dependencies
        foreach (Uri dep in cached.Script.Dependencies)
        {
            await AddDependencyAsync(docUri.ToUri(), dep, languageId);
        }

        // Snapshot dependency info and exported symbols
        var snapshot = await SnapshotDependenciesAsync(cached.Script.Dependencies, languageId, cancellationToken);

        // Merge + analyse under this script's analysis lock
        await WithAnalysisLockAsync(docUri, async () =>
        {
            ApplyDependencySnapshot(cached.Script, snapshot);
            await cached.Script.AnalyseAsync(snapshot.ExportedSymbols, cancellationToken);

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

    // Consolidated: snapshot dependency locations/metadata and exported symbols
    private async Task<DependencySnapshot> SnapshotDependenciesAsync(IEnumerable<Uri> dependencies, string? languageId, CancellationToken cancellationToken)
    {
        var snapshot = new DependencySnapshot();
        foreach (Uri dependency in dependencies)
        {
            var depDoc = DocumentUri.From(dependency);
            if (Scripts.TryGetValue(depDoc, out CachedScript? depScript))
            {
                await EnsureParsedAsync(depDoc, depScript.Script, languageId ?? depScript.Script.LanguageId, cancellationToken);
                // Collect exported symbols
                snapshot.ExportedSymbols.AddRange(await depScript.Script.IssueExportedSymbolsAsync(cancellationToken));

                // Snapshot table data under analysis lock
                await WithAnalysisLockAsync(depDoc, async () =>
                {
                    var depTable = depScript.Script.DefinitionsTable;
                    if (depTable is not null)
                    {
                        snapshot.FunctionLocations.AddRange(depTable.GetAllFunctionLocations());
                        snapshot.ClassLocations.AddRange(depTable.GetAllClassLocations());
                        snapshot.FunctionParameters.AddRange(depTable.GetAllFunctionParameters());
                        snapshot.FunctionDocs.AddRange(depTable.GetAllFunctionDocs());
                        snapshot.FunctionVarargs.AddRange(depTable.GetAllFunctionVarargs());
                    }
                    await Task.CompletedTask;
                });
            }
        }
        return snapshot;
    }

    // Consolidated: apply snapshot to a target script's DefinitionsTable
    private static void ApplyDependencySnapshot(Script target, DependencySnapshot snapshot)
    {
        var table = target.DefinitionsTable;
        if (table is null) return;

        foreach (var kv in snapshot.FunctionLocations)
        {
            var key = kv.Key; var val = kv.Value;
            table.AddFunctionLocation(key.Namespace, key.Name, val.FilePath, val.Range);
        }

        foreach (var kv in snapshot.ClassLocations)
        {
            var key = kv.Key; var val = kv.Value;
            table.AddClassLocation(key.Namespace, key.Name, val.FilePath, val.Range);
        }

        foreach (var kv in snapshot.FunctionParameters)
        {
            var key = kv.Key; var vals = kv.Value;
            table.RecordFunctionParameters(key.Namespace, key.Name, vals);
        }

        foreach (var kv in snapshot.FunctionDocs)
        {
            var key = kv.Key; var doc = kv.Value;
            table.RecordFunctionDoc(key.Namespace, key.Name, doc);
        }

        foreach (var kv in snapshot.FunctionVarargs)
        {
            var key = kv.Key; var hasVararg = kv.Value;
            table.RecordFunctionVararg(key.Namespace, key.Name, hasVararg);
        }
    }
}
