using System.Collections.Concurrent;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
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
/// GSH files inserted by many scripts are lexed exactly once, via the shared
/// <see cref="InsertCache"/>.
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

    /// <summary>
    /// Lexed <c>#insert</c> targets, shared by every file that inserts one.
    ///
    /// The same <see cref="InsertCache"/> the constructor is handed for macros, not a second cache of
    /// the same headers, which would hold BO3's 114 distinct headers twice over.
    ///
    /// Never null: a caller that supplies none gets one of its own. Without it a header is re-read and
    /// re-lexed once per file that inserts it — BO3 writes 2,137 insert directives naming those
    /// 114 headers.
    /// </summary>
    private readonly InsertCache _inserts;

    // Optional persistent cache and its warm-restore snapshot (set via UseCache). The snapshot
    // holds blobs rather than records: see CachedEntry for why the deserialize belongs down here.
    private SqliteCache? _cache;

    /// <summary>
    /// The blobs a warm start may restore from, held only for the duration of an indexing pass.
    ///
    /// Kept for the session it would pin 21 MB of compressed blobs on bo3 and 64 MB on bo1 — this
    /// class is a singleton in the server — long after the last file that could use them was
    /// indexed, against a 400 MB steady-state budget.
    ///
    /// <see cref="IndexAsync"/> releases it on the way out and <see cref="ReloadRestoreSnapshot"/>
    /// puts it back for the one caller that indexes twice. Paying a second 13-54 ms read on a
    /// workspace-folder change is the cheaper side of that trade: the snapshot is live for
    /// milliseconds and dead for hours.
    /// </summary>
    private IReadOnlyDictionary<string, CachedEntry> _restored = s_emptyRestore;

    private static readonly IReadOnlyDictionary<string, CachedEntry> s_emptyRestore =
        new Dictionary<string, CachedEntry>(StringComparer.Ordinal);

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

    /// <summary>
    /// Serialises whole passes: <see cref="IndexAsync"/> is re-entrant on a single shared
    /// instance (the startup pass and a workspace-folder-change re-index both call it on the
    /// SAME <see cref="WorkspaceIndexer"/>), and interleaving two passes corrupts state neither
    /// one owns exclusively — <see cref="_restored"/> can be swapped out from under the pass
    /// reading it, <see cref="_skippedOversized"/> resets mid-count, and
    /// <see cref="ScriptDatabase.MarkIndexComplete"/> can fire from the second pass before the
    /// first has actually finished. A second caller WAITS rather than corrupting anything; a
    /// folder-change re-index arriving during a cold start is an ordinary thing to happen, not a
    /// contention path worth failing.
    /// </summary>
    private readonly SemaphoreSlim _passGate = new(1, 1);

    public WorkspaceIndexer(
        ScriptDatabase database, Func<PathResolver> resolverProvider, IFileSystem fileSystem, NameTable names,
        InsertCache? inserts = null, GameProfile? profile = null)
    {
        _database = database;
        _resolverProvider = resolverProvider;
        _fileSystem = fileSystem;
        _names = names;
        _inserts = inserts ?? new InsertCache();
        _profile = profile;
    }

    /// <summary>
    /// Enables persistent caching: unchanged files restore from <paramref name="restored"/>, fresh
    /// analyses are written to <paramref name="cache"/>.
    ///
    /// The snapshot is consumed by the NEXT indexing pass and released when it finishes. A caller
    /// that indexes again wants <see cref="ReloadRestoreSnapshot"/> first; the cache itself stays
    /// attached either way, so writes continue regardless.
    /// </summary>
    public void UseCache(SqliteCache cache, IReadOnlyDictionary<string, CachedEntry> restored)
    {
        _cache = cache;
        _restored = restored;
    }

    /// <summary>
    /// Re-reads the restore snapshot from the attached cache, for a second indexing pass in the
    /// same session.
    ///
    /// A failed read leaves the snapshot empty rather than throwing: the cache may have been closed
    /// and deleted by <c>gscode/clearCache</c> since it was attached, and the right answer to that
    /// is the cold index the user asked for. It is not silent — the pass that follows reports zero
    /// files restored, which is the same thing the log would say.
    /// </summary>
    public void ReloadRestoreSnapshot()
    {
        try
        {
            _restored = _cache?.LoadAll() ?? s_emptyRestore;
        }
        catch ( Exception exception ) when ( exception is not OutOfMemoryException )
        {
            _restored = s_emptyRestore;
        }
    }

    private PathResolver Resolver
    {
        get { return _resolverProvider(); }
    }

    /// <summary>
    /// Indexes everything the resolver can reach. Returns the number of files indexed.
    ///
    /// One pass at a time — see <see cref="_passGate"/>. A caller that also needs
    /// <see cref="ReloadRestoreSnapshot"/> should ask for it here via
    /// <paramref name="reloadSnapshot"/> rather than calling it separately beforehand: done
    /// outside the gate, a concurrent pass already past the reload could have its snapshot
    /// replaced mid-read by this call's reload landing in between.
    /// </summary>
    /// <param name="ownedByEditor">
    /// Whether a given path is open in the editor, in which case its buffer — already committed
    /// by the text-sync handler — is the source of truth and this pass must not overwrite it with
    /// what disk currently says. Null treats everything as closed, which is correct for a
    /// workspace with nothing open yet. See <see cref="ProcessFile"/> for what "must not
    /// overwrite" means precisely — the cache still gets the disk content either way.
    /// </param>
    public async Task<IndexOutcome> IndexAsync(
        IndexingMode mode, IIndexProgressListener progress, CancellationToken cancellationToken,
        bool reloadSnapshot = false, Func<string, bool>? ownedByEditor = null)
    {
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if ( reloadSnapshot )
            {
                ReloadRestoreSnapshot();
            }

            return await IndexCoreAsync(mode, progress, cancellationToken, ownedByEditor).ConfigureAwait(false);
        }
        finally
        {
            // In a finally rather than after the return, so a cancelled pass releases it too. The
            // folder-change caller passes a real token, and a cancellation there would otherwise
            // pin the whole snapshot for the rest of the session — which is the case this exists
            // to remove.
            _restored = s_emptyRestore;
            _passGate.Release();
        }
    }

    private async Task<IndexOutcome> IndexCoreAsync(
        IndexingMode mode, IIndexProgressListener progress, CancellationToken cancellationToken,
        Func<string, bool>? ownedByEditor)
    {
        if ( mode == IndexingMode.Off )
        {
            return new IndexOutcome(0, 0, 0);
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PerfTracker.Begin("index.total");

        // Enumeration is fully serial and blocks every worker, so it gets its own scope rather than
        // index.total's: on a cold run it is pure I/O with no CPU in flight, and folded in it could
        // not be told apart from the analysis it precedes.
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
            FileOutcome outcome = ProcessFile(path, allowRestore: true, ownedByEditor);
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

        // Phase two: re-parse restored files whose inserted headers actually changed — including a
        // header CREATED or DELETED between sessions, or one that now resolves to a different file
        // (a mod copy that started or stopped shadowing raw). A restored record's insert edge
        // carries the RESOLUTION FROM LAST SESSION: comparing against that stored resolution rather
        // than a freshly computed one misses exactly these cases. An insert that failed to resolve
        // last session stores "" as its ResolvedPath, which is never a real header's path — so a
        // header appearing would never match, and a header vanishing would never reach
        // changedHeaders, since a deleted file is not among the targets this pass sees.
        if ( restoredRecords.Count > 0 )
        {
            List<ScriptRecord> restoredList = [.. restoredRecords];

            // Whether this insert edge's target has changed since last session: its resolved path
            // moved (created, deleted, or now shadowed differently), or it resolves to a header this
            // pass freshly analysed. Resolve() is memoized per (context, path), so re-resolving costs
            // nothing beyond the first insert edge naming a given header.
            bool EdgeChanged(ScriptRecord record, DependencyEdge edge)
            {
                string fresh = Resolver.Resolve(Resolver.GetContext(record.Path), edge.RawPath) ?? "";
                return fresh != edge.ResolvedPath || changedHeaders.ContainsKey(fresh);
            }

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
                        if ( edge.IsInsert && EdgeChanged(record, edge) )
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
                    if ( edge.IsInsert && EdgeChanged(record, edge) )
                    {
                        stale.Add(record.Path);
                        break;
                    }
                }
            }

            foreach ( string path in stale )
            {
                ProcessFile(path, allowRestore: false, ownedByEditor);
                reparsedAfterHeaderChange++;
            }
        }

        PerfTracker.End();
        progress.Completed(completed, targets.Count, stopwatch.Elapsed);

        // Every cache row for a file this pass did NOT find on disk: deleted, renamed, or moved
        // out from under a resolver whose roots changed, while the server was not running to see
        // the watcher event that would otherwise have pruned it via RemoveFile. EnumerateIndexTargets
        // always returns the workspace's COMPLETE file set — Off is the only mode this method treats
        // differently, so this is safe on every pass that reaches here — so anything in _restored
        // that is not in it is stale in every one of them, not just a cold one, and would otherwise
        // sit in the database forever: nothing else ever asks this question.
        if ( _cache is not null && _restored.Count > 0 )
        {
            HashSet<string> currentPaths = new(StringComparer.Ordinal);
            foreach ( string path in targets )
            {
                currentPaths.Add(PathUtil.NormalizeAbsolute(path));
            }

            foreach ( string cachedPath in _restored.Keys )
            {
                if ( !currentPaths.Contains(cachedPath) )
                {
                    _cache.EnqueueDelete(cachedPath);
                }
            }
        }

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

    /// <summary>
    /// Re-reads and re-parses one file WITHOUT committing anything — for the full-mode lint
    /// sweep, which needs a <see cref="ParseResult"/> purely to run the cross-file lints (records
    /// do not retain one) and must not perturb the database's structural state doing it. The
    /// sweep already knows the record it is refreshing; this only has to reproduce its parse.
    ///
    /// Null on a read failure or an oversized file — the same two cases <see cref="ProcessFile"/>
    /// treats as "nothing to analyse here" on the indexing path.
    /// </summary>
    public ParseResult? AnalyzeForLintSweep(string path)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);

        string content;
        try
        {
            content = _fileSystem.ReadAllText(normalized);
        }
        catch ( IOException )
        {
            return null;
        }
        catch ( UnauthorizedAccessException )
        {
            return null;
        }

        if ( content.Length > MaxAnalysedCharacters )
        {
            return null;
        }

        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);
        ResolutionContext context = Resolver.GetContext(normalized);

        return ScriptAnalysis.Analyze(
            normalized,
            language,
            SourceText.From(content),
            new ResolverInsertProvider(Resolver, context, _fileSystem, _inserts),
            _names,
            profile: _profile,
            headerCache: _inserts);
    }

    private FileOutcome ProcessFile(string path, bool allowRestore, Func<string, bool>? ownedByEditor = null)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);

        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);

        // Checked once and reused below: an open document's buffer is the source of truth, and
        // the text-sync handler has already committed it. Reading DISK is still worth doing here
        // — it is what the cache below wants, and what a GSH's insert-cache seeding wants — the
        // one thing it must not do is overwrite the live record the editor owns, which on a cold
        // start it would otherwise do unconditionally, seconds after (or before) the open
        // document's own commit lands, whichever direction the race goes.
        bool ownedByOpenDocument = ownedByEditor is not null && ownedByEditor(normalized);

        // Read BEFORE the content, and only for the file the seed below can apply to. A stamp taken
        // afterwards would date a write that landed between the two as already seen, and the seeded
        // entry would then stay stale until the file changed a second time.
        DateTime headerStamp = language == ScriptLanguage.Gsh
            ? _fileSystem.GetLastWriteTimeUtc(normalized)
            : default;

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
        //
        // The hash is checked BEFORE the record is materialised, which is the whole point of
        // holding blobs rather than records: a file that has changed costs one hash here and never
        // pays the inflation or the record read behind it. On a genuinely warm start that saves
        // nothing, since every file matches — what it saves is doing all of that work serially at
        // startup, ahead of this loop, instead of on the loop's own threads.
        if ( allowRestore && _restored.TryGetValue(normalized, out CachedEntry? cached) )
        {
            PerfTracker.Begin("index.restore");

            ScriptRecord? restored = null;
            if ( cached.ContentHash == ScriptDatabase.ComputeContentHash(content) )
            {
                // Null when the blob is corrupt, which falls through to a normal analysis below
                // rather than failing the file — the same outcome a missing cache entry has.
                restored = cached.Materialize();
                if ( restored is not null )
                {
                    // Both are pure functions of (path, current RootConfig), same as the
                    // fresh-analysis path below always computes — recomputed here too, since a
                    // workspace-folder or setting change between sessions can reclassify a file
                    // whose bytes never changed (raw becoming a mod, one mod folder becoming
                    // another), and the cached blob only ever carries LAST session's answer.
                    ResolutionContext restoredContext = Resolver.GetContext(normalized);
                    string freshContextId = ScriptDatabase.ContextIdOf(restoredContext);
                    string freshRelativePath = Resolver.GetScriptRelativePath(normalized, restoredContext);

                    if ( restored.ContextId != freshContextId || restored.RelativePath != freshRelativePath )
                    {
                        restored = restored with { ContextId = freshContextId, RelativePath = freshRelativePath };
                    }

                    // See DiskStillMatches: a full cold-start pass runs on a background task while
                    // the server keeps handling watched-file events, so disk can move again while
                    // this call is still in flight. Skipping the store write here, not the whole
                    // restore, is what keeps this call from winning a last-write race against a
                    // fresher commit the corresponding watched-file event already made (or is
                    // about to make) for this exact path.
                    if ( !ownedByOpenDocument && DiskStillMatches(normalized, content) )
                    {
                        _database.CommitRecord(restored);
                    }
                }
            }

            PerfTracker.End();

            if ( restored is not null )
            {
                return new FileOutcome(Restored: true, Record: restored);
            }
        }

        ResolutionContext context = Resolver.GetContext(normalized);

        PerfTracker.Begin("index.analyse");
        ParseResult result = ScriptAnalysis.Analyze(
            normalized,
            language,
            SourceText.From(content),
            new ResolverInsertProvider(Resolver, context, _fileSystem, _inserts),
            _names,
            profile: _profile,
            headerCache: _inserts);
        PerfTracker.End();

        // A header is an index target in its own right (it matches *.gsh) AND an insert source, and
        // the analysis above has already produced exactly what the insert path would go on to build
        // from scratch. See InsertCache.SeedIfAbsent for why it is offered rather than assigned.
        if ( result.Language == ScriptLanguage.Gsh )
        {
            _inserts.SeedIfAbsent(
                normalized, new InsertedFile(normalized, result.Text, result.Lexed.Tokens), headerStamp);
        }

        string relativePath = Resolver.GetScriptRelativePath(normalized, context);

        // Own the record either way — the cache below wants disk's actual content regardless of
        // who is open — but STORE it only when nobody has it open: an open document's buffer was
        // already committed as isDirty by the text-sync handler, and this pass has no way to know
        // whether that commit landed before or after this read, so it must never guess by
        // overwriting. BuildRecord does the same work Commit does, minus the store write; the
        // watched-file path skips the same way for the same reason (WatchedFileUpdater.Apply).
        PerfTracker.Begin("index.commit");
        // Same race as the restore branch above, but the window is far wider here: lex, parse
        // and extract sit between the read at the top of this call and this commit, so a file
        // that changes again during that stretch is not a corner case on a slow cold index. A
        // mismatch here does not mean this call FAILED to index the file — it means the fresher
        // watched-file event for that later change already committed (or is about to), and this
        // now-stale analysis must not win a last-write race against it. Building rather than
        // committing keeps the record usable by this call's own caller without touching the
        // store the fresher event owns.
        ScriptRecord record = ownedByOpenDocument || !DiskStillMatches(normalized, content)
            ? ScriptDatabase.BuildRecord(result, context, isDirty: false, relativePath)
            : _database.Commit(result, context, isDirty: false, relativePath);
        PerfTracker.End();

        PerfTracker.Begin("index.enqueue");
        _cache?.Enqueue(record);
        PerfTracker.End();

        return new FileOutcome(Restored: false, Record: record);
    }

    /// <summary>
    /// Whether disk still holds the exact bytes an analysis was built from — checked immediately
    /// before that analysis is committed to the store, never before. Startup indexing runs on a
    /// background task while the server keeps handling watched-file events (see Program.cs), so
    /// a file can change again while one <see cref="ProcessFile"/> call is still in flight; the
    /// watched-file event for that later change applies (or will apply) on its own thread, with
    /// no gate between it and a concurrent indexing pass. Without this check, the store's
    /// last-write-wins Upsert means whichever call reaches it LAST wins regardless of
    /// which one read the fresher content — so this call's own now-stale write can silently
    /// clobber a commit the fresher event already made.
    ///
    /// A mismatch is not this call failing to index the file: the change that caused it is
    /// exactly the change the other event exists to apply, so skipping the store write here
    /// leaves that file no less indexed than it already is (or is about to be) on that thread.
    /// </summary>
    private bool DiskStillMatches(string normalizedPath, string analysedContent)
    {
        try
        {
            return _fileSystem.ReadAllText(normalizedPath) == analysedContent;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            // An unreadable recheck almost always means "deleted mid-analysis" — and a genuine
            // deletion is caught by its own watched-file event regardless of what this call does,
            // so there is nothing safer to do here than let the commit through as analysed.
            return true;
        }
    }

    /// <summary>
    /// Drops a GSH from the insert cache (called when the file changes on disk).
    ///
    /// The timestamp check inside the cache is the backstop and would catch this on its own; this
    /// is the fast path for a caller that already knows. It now also drops the header's walked
    /// CONTRIBUTION, which the local cache it replaced could not reach — that was left to the same
    /// timestamp check, one call later.
    /// </summary>
    public void InvalidateGsh(string normalizedPath)
    {
        _inserts.Invalidate(normalizedPath);
    }

    /// <summary>
    /// Announces that a header appeared or vanished, rather than that one changed.
    ///
    /// <see cref="InvalidateGsh"/> cannot speak for this. It reports a header whose CONTENT moved,
    /// and says nothing when the cache holds no copy — correct, since a header nobody has read
    /// cannot have been expanded into anyone's parse. A header that did not exist a moment ago is
    /// exactly that case and still changes what an insert path resolves to, both for the file that
    /// could not resolve it at all and for the one whose raw header a new mod copy now shadows.
    /// </summary>
    public void NoteHeaderSetChanged()
    {
        _inserts.NoteHeaderSetChanged();
    }

    /// <summary>
    /// Forgets every memoized answer the live resolver has given out — see
    /// <see cref="PathResolver.InvalidateResolutionCache"/>. Indirected through the indexer, like
    /// every other resolver access here, so a caller never has to know the resolver is reached
    /// through <see cref="_resolverProvider"/> rather than held directly.
    /// </summary>
    public void InvalidateResolutionCache()
    {
        _resolverProvider().InvalidateResolutionCache();
    }

    /// <summary>Removes a deleted file from the database, persistent cache, and insert cache.</summary>
    public void RemoveFile(string normalizedPath, ScriptLanguage language)
    {
        _database.Remove(normalizedPath, language);
        _cache?.EnqueueDelete(normalizedPath);
        if ( language == ScriptLanguage.Gsh )
        {
            _inserts.Invalidate(normalizedPath);
        }
    }
}
