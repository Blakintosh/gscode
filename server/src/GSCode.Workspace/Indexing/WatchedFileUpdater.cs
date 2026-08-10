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
    /// <param name="ownedByEditor">
    /// Whether the file is OPEN, in which case its buffer is the source of truth and the text-sync
    /// handler already analyses it on open, change and save.
    ///
    /// Re-indexing it here would read DISK behind that buffer, and the two are not the same thing:
    /// with unsaved edits it replaces the record the editor just committed with older content, so
    /// every other file's resolution silently describes text the user is not looking at until the
    /// next keystroke puts it back. On a plain save it is merely the same file parsed twice, once
    /// from the buffer and once from disk.
    ///
    /// The file's own record is all that is skipped. A header's SIDE EFFECTS still run: dropping
    /// the cached lex and re-indexing everything that inserts it are facts about OTHER files, and
    /// those files are not open just because the header is.
    /// </param>
    public IReadOnlyList<string> Apply(string path, WatchedFileChange change, bool ownedByEditor = false)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);

        if ( change == WatchedFileChange.Deleted )
        {
            // A deleted file that is still open keeps its record: the buffer outlives the file on
            // disk, and dropping it would break every lookup into a document the user can still see
            // and save back. Closing it is what retires the record.
            if ( !ownedByEditor )
            {
                _indexer.RemoveFile(normalized, language);
            }

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
            if ( !ownedByEditor )
            {
                _indexer.IndexFile(normalized);
            }

            return ReindexInserters(normalized);
        }

        if ( ownedByEditor )
        {
            return [];
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
