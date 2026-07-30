using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;

namespace GSCode.Workspace.Indexing;

/// <summary>Background indexing depth (mirrors gscode.workspaceIndexingMode).</summary>
public enum IndexingMode
{
    Off,
    Partial,
    Full,
}

/// <summary>
/// What an indexing pass did. The restored/analysed split is what distinguishes a cold run
/// from a warm one, and the two have very different allocation profiles.
/// </summary>
/// <param name="Total">Files processed.</param>
/// <param name="Restored">Files served from the cache without re-analysis.</param>
/// <param name="Analysed">Files taken through the full lex/preprocess/parse/extract pipeline.</param>
/// <param name="SkippedOversized">Files past the size limit, left unanalysed.</param>
public readonly record struct IndexOutcome(int Total, int Restored, int Analysed, int SkippedOversized = 0);

/// <summary>Receives indexing lifecycle events (the server maps these to notifications).</summary>
public interface IIndexProgressListener
{
    void Started(int totalFiles);

    /// <summary>Fired on every file completion; implementations throttle the wire traffic.</summary>
    void Progressed(int filesIndexed, int totalFiles);

    /// <summary>
    /// One file finished, with how long it took and whether the cache spared the analysis.
    ///
    /// This exists so the server can log per-file timings without GSCode.Workspace taking a
    /// dependency on Serilog — the layering keeps logging on the server side, so the indexer
    /// reports through the listener it already has rather than acquiring a logger.
    ///
    /// Called on the parallel indexing path, so implementations must be cheap and thread-safe.
    /// </summary>
    void FileIndexed(string path, TimeSpan elapsed, bool restoredFromCache);

    void Completed(int filesIndexed, int totalFiles, TimeSpan elapsed);
}

/// <summary>A listener for contexts that don't care (tests, indexing off).</summary>
public sealed class NullIndexProgressListener : IIndexProgressListener
{
    public static NullIndexProgressListener Instance { get; } = new();

    public void Started(int totalFiles)
    {
    }

    public void FileIndexed(string path, TimeSpan elapsed, bool restoredFromCache)
    {
    }

    public void Progressed(int filesIndexed, int totalFiles)
    {
    }

    public void Completed(int filesIndexed, int totalFiles, TimeSpan elapsed)
    {
    }
}

/// <summary>
/// Cold-start indexing: enumerate every reachable script, run the per-file pipeline
/// under bounded parallelism (across files, never within one), and commit records.
/// GSH files inserted by many scripts are lexed exactly once via a Lazy cache.
/// </summary>
public sealed class WorkspaceIndexer
{
    private readonly ScriptDatabase _database;
    private readonly Func<PathResolver> _resolverProvider;
    private readonly IFileSystem _fileSystem;
    private readonly NameTable _names;

    /// <summary>
    /// Largest file the pipeline will analyse. Generous next to real scripts (the biggest stock
    /// GSC is well under a megabyte), so it only ever catches genuinely pathological input.
    /// </summary>
    public const int MaxAnalysedCharacters = 8 * 1024 * 1024;

    private int _skippedOversized;

    // path → lazily lexed insert target, shared by every file that inserts it.
    private readonly ConcurrentDictionary<string, Lazy<InsertedFile?>> _gshCache = new(StringComparer.Ordinal);

    // Optional persistent cache and its cold-restore snapshot (set via UseCache).
    private SqliteCache? _cache;
    private IReadOnlyDictionary<string, ScriptRecord> _restored = new Dictionary<string, ScriptRecord>();

    /// <summary>Reads the current resolver each call, so resolver swaps take effect immediately.</summary>
    private readonly IHeaderMacroCache? _headerCache;

    public WorkspaceIndexer(
        ScriptDatabase database, Func<PathResolver> resolverProvider, IFileSystem fileSystem, NameTable names,
        IHeaderMacroCache? headerCache = null)
    {
        _database = database;
        _resolverProvider = resolverProvider;
        _fileSystem = fileSystem;
        _names = names;
        _headerCache = headerCache;
    }

    /// <summary>Enables persistent caching: unchanged files restore from <paramref name="restored"/>, fresh analyses are written to <paramref name="cache"/>.</summary>
    public void UseCache(SqliteCache cache, IReadOnlyDictionary<string, ScriptRecord> restored)
    {
        _cache = cache;
        _restored = restored;
    }

    private PathResolver Resolver
    {
        get { return _resolverProvider(); }
    }

    /// <summary>Indexes everything the resolver can reach. Returns the number of files indexed.</summary>
    public async Task<IndexOutcome> IndexAsync(IndexingMode mode, IIndexProgressListener progress, CancellationToken cancellationToken)
    {
        if ( mode == IndexingMode.Off )
        {
            return new IndexOutcome(0, 0, 0);
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PerfTracker.Begin("index.total");

        List<string> targets = [.. Resolver.EnumerateIndexTargets()];
        progress.Started(targets.Count);

        int completed = 0;
        _skippedOversized = 0;
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellationToken,
        };

        // GSH headers that were re-parsed (content changed since the cache was written);
        // restored files that #insert one of these must be re-parsed in phase two.
        ConcurrentDictionary<string, byte> changedHeaders = new(StringComparer.Ordinal);
        ConcurrentBag<ScriptRecord> restoredRecords = [];
        int reparsedAfterHeaderChange = 0;

        await Parallel.ForEachAsync(targets, options, (path, token) =>
        {
            token.ThrowIfCancellationRequested();

            long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FileOutcome outcome = ProcessFile(path, allowRestore: true);
            progress.FileIndexed(path, System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks), outcome.Restored);

            if ( outcome.Restored && outcome.Record is not null )
            {
                restoredRecords.Add(outcome.Record);
            }
            else if ( outcome.Record is { Language: ScriptLanguage.Gsh } )
            {
                changedHeaders.TryAdd(outcome.Record.Path, 0);
            }

            int done = Interlocked.Increment(ref completed);
            progress.Progressed(done, targets.Count);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        // Phase two: re-parse restored files whose inserted headers actually changed.
        if ( !changedHeaders.IsEmpty )
        {
            List<string> stale = [];
            foreach ( ScriptRecord record in restoredRecords )
            {
                foreach ( DependencyEdge edge in record.Dependencies )
                {
                    if ( edge.IsInsert && changedHeaders.ContainsKey(edge.ResolvedPath) )
                    {
                        stale.Add(record.Path);
                        break;
                    }
                }
            }

            foreach ( string path in stale )
            {
                ProcessFile(path, allowRestore: false);
                reparsedAfterHeaderChange++;
            }
        }

        PerfTracker.End();
        progress.Completed(completed, targets.Count, stopwatch.Elapsed);

        // Restored files that phase two re-parsed were analysed after all, so they count as
        // analysed rather than restored — the split is what tells a cold run from a warm one.
        int restored = restoredRecords.Count - reparsedAfterHeaderChange;
        // Function resolution cannot speak until this point: before it, every script function in
        // the workspace looks nonexistent.
        _database.MarkIndexComplete();

        return new IndexOutcome(completed, restored, completed - restored, _skippedOversized);
    }

    /// <summary>Outcome of processing one file: whether it came from cache, and the resulting record.</summary>
    private readonly record struct FileOutcome(bool Restored, ScriptRecord? Record);

    /// <summary>Analyses one file from disk and commits its record (also used by the watcher).</summary>
    public ScriptRecord? IndexFile(string path)
    {
        return ProcessFile(path, allowRestore: false).Record;
    }

    private FileOutcome ProcessFile(string path, bool allowRestore)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);

        string content;
        try
        {
            content = _fileSystem.ReadAllText(normalized);
        }
        catch ( IOException )
        {
            return new FileOutcome(Restored: false, Record: null);
        }
        catch ( UnauthorizedAccessException )
        {
            return new FileOutcome(Restored: false, Record: null);
        }

        if ( content.Length > MaxAnalysedCharacters )
        {
            // Reading is cheap; lex/parse/extract on a file this size is not. Skipping keeps a
            // single pathological file from dominating a cold index. Real scripts are orders of
            // magnitude smaller, so this should never fire in practice. Counted rather than
            // logged here: this layer has no logger by design, so the server reports it.
            Interlocked.Increment(ref _skippedOversized);
            return new FileOutcome(Restored: false, Record: null);
        }

        // Restore from cache when the on-disk content matches what was analysed before.
        if ( allowRestore && _restored.TryGetValue(normalized, out ScriptRecord? cached) )
        {
            if ( cached.ContentHash == ScriptDatabase.ComputeContentHash(content) )
            {
                _database.CommitRecord(cached);
                return new FileOutcome(Restored: true, Record: cached);
            }
        }

        ResolutionContext context = Resolver.GetContext(normalized);
        ParseResult result = ScriptAnalysis.Analyze(
            normalized,
            ScriptAnalysis.LanguageFromPath(normalized),
            SourceText.From(content),
            new CachingInsertProvider(this, context),
            _names,
            profile: null,
            headerCache: _headerCache);

        string relativePath = Resolver.GetScriptRelativePath(normalized, context);
        ScriptRecord record = _database.Commit(result, context, isDirty: false, relativePath);
        _cache?.Enqueue(record);
        return new FileOutcome(Restored: false, Record: record);
    }

    /// <summary>Drops a GSH from the lex cache (called when the file changes on disk).</summary>
    public void InvalidateGsh(string normalizedPath)
    {
        _gshCache.TryRemove(normalizedPath, out _);
    }

    /// <summary>Removes a deleted file from the database, persistent cache, and GSH lex cache.</summary>
    public void RemoveFile(string normalizedPath, ScriptLanguage language)
    {
        _database.Remove(normalizedPath, language);
        _cache?.EnqueueDelete(normalizedPath);
        if ( language == ScriptLanguage.Gsh )
        {
            _gshCache.TryRemove(normalizedPath, out _);
        }
    }

    private InsertedFile? LoadInsert(string rawInsertPath, ResolutionContext context)
    {
        string? resolved = Resolver.Resolve(context, rawInsertPath);
        if ( resolved is null )
        {
            return null;
        }

        Lazy<InsertedFile?> lazy = _gshCache.GetOrAdd(resolved, key => new Lazy<InsertedFile?>(() =>
        {
            try
            {
                SourceText text = SourceText.From(_fileSystem.ReadAllText(key));
                return new InsertedFile(key, text, Lexer.Lex(text).Tokens);
            }
            catch ( IOException )
            {
                return null;
            }
            catch ( UnauthorizedAccessException )
            {
                return null;
            }
        }));

        return lazy.Value;
    }

    /// <summary>Insert provider backed by the shared Lazy GSH cache.</summary>
    private sealed class CachingInsertProvider : IInsertProvider
    {
        private readonly WorkspaceIndexer _indexer;
        private readonly ResolutionContext _context;

        public CachingInsertProvider(WorkspaceIndexer indexer, ResolutionContext context)
        {
            _indexer = indexer;
            _context = context;
        }

        public bool TryGetInsert(string rawInsertPath, out InsertedFile inserted)
        {
            InsertedFile? loaded = _indexer.LoadInsert(rawInsertPath, _context);
            inserted = loaded!;
            return loaded is not null;
        }
    }
}
