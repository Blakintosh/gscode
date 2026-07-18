using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Database;

namespace GSCode.Workspace.Indexing;

/// <summary>How a watched file changed.</summary>
public enum WatchedFileChange
{
    Created,
    Changed,
    Deleted,
}

/// <summary>
/// Applies on-disk file changes to the database: re-index created/changed files, drop
/// deleted ones, and when a GSH changes invalidate its lex cache and re-index every
/// file that #inserts it (so macro edits propagate).
/// </summary>
public sealed class WatchedFileUpdater
{
    private readonly ScriptDatabase _database;
    private readonly WorkspaceIndexer _indexer;

    public WatchedFileUpdater(ScriptDatabase database, WorkspaceIndexer indexer)
    {
        _database = database;
        _indexer = indexer;
    }

    /// <summary>Applies one file change; returns the paths whose diagnostics may need republishing.</summary>
    public IReadOnlyList<string> Apply(string path, WatchedFileChange change)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);

        if ( change == WatchedFileChange.Deleted )
        {
            _indexer.RemoveFile(normalized, language);
            if ( language == ScriptLanguage.Gsh )
            {
                return ReindexInserters(normalized);
            }

            return [];
        }

        // Created or changed.
        if ( language == ScriptLanguage.Gsh )
        {
            // Re-analysing dependents re-reads the header, so drop the stale lexed copy first.
            _indexer.InvalidateGsh(normalized);
            _indexer.IndexFile(normalized);
            return ReindexInserters(normalized);
        }

        _indexer.IndexFile(normalized);
        return [normalized];
    }

    private IReadOnlyList<string> ReindexInserters(string normalizedGshPath)
    {
        List<string> touched = [];
        foreach ( string dependent in _database.FilesInserting(normalizedGshPath).ToList() )
        {
            _indexer.IndexFile(dependent);
            touched.Add(dependent);
        }

        return touched;
    }
}
