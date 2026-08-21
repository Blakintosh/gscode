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
/// <param name="Enumerate">
/// How long it took to find the files. SERIAL, and it blocks every worker — a workspace folder
/// that contains the whole game install is 295,640 files to find 1,105 scripts, and that showed up
/// as slow "indexing" with no way to tell it from the analysis.
/// </param>
/// <param name="Analyse">Wall-clock for the parallel pass: reading, analysing and committing.</param>
/// <param name="ThreadTime">
/// The per-file elapsed times summed. Against <paramref name="Analyse"/> it gives the parallel
/// speedup actually achieved, which is the number that separates "each file is slow" from "the
/// threads are not running".
/// </param>
public readonly record struct IndexOutcome(
    int Total,
    int Restored,
    int Analysed,
    int SkippedOversized = 0,
    TimeSpan Enumerate = default,
    TimeSpan Analyse = default,
    TimeSpan ThreadTime = default)
{
    /// <summary>Thread-time over analysis wall-clock: 1x means the parallelism bought nothing.</summary>
    public double Parallelism
    {
        get { return Analyse.TotalMilliseconds <= 0 ? 0 : ThreadTime.TotalMilliseconds / Analyse.TotalMilliseconds; }
    }
}

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

    /// <summary>
    /// The dialect every indexed file is parsed as. Null defers to <see cref="GameProfile.Active"/>
    /// at analysis time, which is what the server wants — it selects the game once, at startup.
    ///
    /// It is a parameter at all because reading Active unconditionally made this silently wrong for
    /// anyone holding a profile in hand. A workspace indexed under the wrong dialect does not fail:
    /// under BO3 a keyword-less <c>is_coop()</c> is not a declaration, so the store comes back EMPTY
    /// and every assertion about what it contains passes for the wrong reason. Two test files worked
    /// around that by building records by hand rather than indexing.
    /// </summary>
    private readonly GameProfile? _profile;

    public WorkspaceIndexer(
        ScriptDatabase database, Func<PathResolver> resolverProvider, IFileSystem fileSystem, NameTable names,
        IHeaderMacroCache? headerCache = null, GameProfile? profile = null)
    {
        _database = database;
        _resolverProvider = resolverProvider;
        _fileSystem = fileSystem;
        _names = names;
        _headerCache = headerCache;
        _profile = profile;
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

        // Enumeration is fully serial and blocks every worker, so it gets its own scope: on a cold
        // run it is pure I/O with no CPU in flight, and it was previously folded into index.total
        // where it could not be told apart from the analysis it precedes.
        PerfTracker.Begin("index.enumerate");
        List<string> targets = [.. Resolver.EnumerateIndexTargets()];
        PerfTracker.End();
        TimeSpan enumerate = stopwatch.Elapsed;

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

        // Summed across the workers, so it is thread-time rather than wall-clock. The listener is
        // handed the same figure per file, but only for logging — this is the total the outcome
        // reports, so a caller gets the parallelism without having to add up a thousand log lines.
        long threadTicks = 0;

        await Parallel.ForEachAsync(targets, options, (path, token) =>
        {
            token.ThrowIfCancellationRequested();

            long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            FileOutcome outcome = ProcessFile(path, allowRestore: true);
            TimeSpan fileElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks);
            Interlocked.Add(ref threadTicks, fileElapsed.Ticks);
            progress.FileIndexed(path, fileElapsed, outcome.Restored);

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
            List<ScriptRecord> restoredList = [.. restoredRecords];

            // A restored header that inserts a changed one changes too — its own bytes are
            // identical, which is exactly why it restored, but what it contributes is not. Close
            // the changed set over the header insert graph before asking who is stale: computing
            // 'stale' straight off the analysed headers reaches one hop only, so in a
            // base.gsh -> wrapper.gsh -> script.gsc chain the script keeps the record built
            // against the OLD macro values for the rest of the session.
            bool grew = true;
            while ( grew )
            {
                grew = false;
                foreach ( ScriptRecord record in restoredList )
                {
                    if ( record.Language != ScriptLanguage.Gsh || changedHeaders.ContainsKey(record.Path) )
                    {
                        continue;
                    }

                    foreach ( DependencyEdge edge in record.Dependencies )
                    {
                        if ( edge.IsInsert && changedHeaders.ContainsKey(edge.ResolvedPath) )
                        {
                            changedHeaders.TryAdd(record.Path, 0);
                            grew = true;
                            break;
                        }
                    }
                }
            }

            List<string> stale = [];
            foreach ( ScriptRecord record in restoredList )
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

        return new IndexOutcome(
            completed,
            restored,
            completed - restored,
            _skippedOversized,
            Enumerate: enumerate,
            Analyse: stopwatch.Elapsed - enumerate,
            ThreadTime: TimeSpan.FromTicks(Interlocked.Read(ref threadTicks)));
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

        // The scopes below split the per-file cost the way the cold path actually spends it: read is
        // blocking I/O inside the parallel body, analyse is the four-phase pipeline, commit is where
        // the store's single write gate is waited on, and enqueue hands off to the cache writer.
        // Every one is [Conditional] and absent from an ordinary build.
        string content;
        PerfTracker.Begin("index.read");
        try
        {
            content = _fileSystem.ReadAllText(normalized);
        }
        catch ( IOException )
        {
            PerfTracker.End();
            return new FileOutcome(Restored: false, Record: null);
        }
        catch ( UnauthorizedAccessException )
        {
            PerfTracker.End();
            return new FileOutcome(Restored: false, Record: null);
        }

        PerfTracker.End();

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
            PerfTracker.Begin("index.restore");
            bool matches = cached.ContentHash == ScriptDatabase.ComputeContentHash(content);
            if ( matches )
            {
                _database.CommitRecord(cached);
            }

            PerfTracker.End();

            if ( matches )
            {
                return new FileOutcome(Restored: true, Record: cached);
            }
        }

        ResolutionContext context = Resolver.GetContext(normalized);

        PerfTracker.Begin("index.analyse");
        ParseResult result = ScriptAnalysis.Analyze(
            normalized,
            ScriptAnalysis.LanguageFromPath(normalized),
            SourceText.From(content),
            new CachingInsertProvider(this, context),
            _names,
            profile: _profile,
            headerCache: _headerCache);
        PerfTracker.End();

        // A header is an index target in its own right (it matches *.gsh) AND an insert source, and
        // those two paths each read and lexed it independently — the analysis above has already
        // produced exactly what LoadInsert would go on to build from scratch. Seed it here instead.
        //
        // GetOrAdd rather than an assignment because the race is real and unordered: a .gsc that
        // inserts this header may be processed first and populate the entry itself. Whoever arrives
        // first wins, and the two would produce identical content anyway — same file, same lexer,
        // same profile. So this halves the header work rather than eliminating it.
        if ( result.Language == ScriptLanguage.Gsh )
        {
            _gshCache.GetOrAdd(
                normalized,
                new Lazy<InsertedFile?>(new InsertedFile(normalized, result.Text, result.Lexed.Tokens)));
        }

        string relativePath = Resolver.GetScriptRelativePath(normalized, context);

        PerfTracker.Begin("index.commit");
        ScriptRecord record = _database.Commit(result, context, isDirty: false, relativePath);
        PerfTracker.End();

        PerfTracker.Begin("index.enqueue");
        _cache?.Enqueue(record);
        PerfTracker.End();

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

        public bool TryResolveInsertPath(string rawInsertPath, out string resolvedPath)
        {
            string? resolved = _indexer.Resolver.Resolve(_context, rawInsertPath);
            resolvedPath = resolved ?? "";
            return resolved is not null;
        }
    }
}
