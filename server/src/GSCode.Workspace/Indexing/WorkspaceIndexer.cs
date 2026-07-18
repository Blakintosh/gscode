using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
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

/// <summary>Receives indexing lifecycle events (the server maps these to notifications).</summary>
public interface IIndexProgressListener
{
    void Started(int totalFiles);

    /// <summary>Fired on every file completion; implementations throttle the wire traffic.</summary>
    void Progressed(int filesIndexed, int totalFiles);

    void Completed(int filesIndexed, int totalFiles, TimeSpan elapsed);
}

/// <summary>A listener for contexts that don't care (tests, indexing off).</summary>
public sealed class NullIndexProgressListener : IIndexProgressListener
{
    public static NullIndexProgressListener Instance { get; } = new();

    public void Started(int totalFiles)
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
    private readonly PathResolver _resolver;
    private readonly IFileSystem _fileSystem;
    private readonly NameTable _names;

    // path → lazily lexed insert target, shared by every file that inserts it.
    private readonly ConcurrentDictionary<string, Lazy<InsertedFile?>> _gshCache = new(StringComparer.Ordinal);

    public WorkspaceIndexer(ScriptDatabase database, PathResolver resolver, IFileSystem fileSystem, NameTable names)
    {
        _database = database;
        _resolver = resolver;
        _fileSystem = fileSystem;
        _names = names;
    }

    /// <summary>Indexes everything the resolver can reach. Returns the number of files indexed.</summary>
    public async Task<int> IndexAsync(IndexingMode mode, IIndexProgressListener progress, CancellationToken cancellationToken)
    {
        if ( mode == IndexingMode.Off )
        {
            return 0;
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        PerfTracker.Begin("index.total");

        List<string> targets = [.. _resolver.EnumerateIndexTargets()];
        progress.Started(targets.Count);

        int completed = 0;
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(targets, options, (path, token) =>
        {
            token.ThrowIfCancellationRequested();
            IndexFile(path);

            int done = Interlocked.Increment(ref completed);
            progress.Progressed(done, targets.Count);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        PerfTracker.End();
        progress.Completed(completed, targets.Count, stopwatch.Elapsed);
        return completed;
    }

    /// <summary>Analyses one file from disk and commits its record (also used by the watcher).</summary>
    public ScriptRecord? IndexFile(string path)
    {
        string content;
        try
        {
            content = _fileSystem.ReadAllText(path);
        }
        catch ( IOException )
        {
            return null;
        }
        catch ( UnauthorizedAccessException )
        {
            return null;
        }

        ResolutionContext context = _resolver.GetContext(path);
        ParseResult result = ScriptAnalysis.Analyze(
            path,
            ScriptAnalysis.LanguageFromPath(path),
            SourceText.From(content),
            new CachingInsertProvider(this, context),
            _names);

        string relativePath = _resolver.GetScriptRelativePath(path, context);
        return _database.Commit(result, context, isDirty: false, relativePath);
    }

    /// <summary>Drops a GSH from the lex cache (called when the file changes on disk).</summary>
    public void InvalidateGsh(string normalizedPath)
    {
        _gshCache.TryRemove(normalizedPath, out _);
    }

    private InsertedFile? LoadInsert(string rawInsertPath, ResolutionContext context)
    {
        string? resolved = _resolver.Resolve(context, rawInsertPath);
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
