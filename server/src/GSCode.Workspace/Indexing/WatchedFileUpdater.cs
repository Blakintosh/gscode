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
    /// Whether a given path is OPEN, in which case its buffer is the source of truth and the
    /// text-sync handler already analyses it on open, change and save. Null treats everything as
    /// closed. A predicate rather than a flag because the changed file is not the only record this
    /// call rewrites — a header's dependents are re-indexed too, and any of them can be open.
    ///
    /// Re-indexing an open file here would read DISK behind its buffer, and the two are not the
    /// same thing: with unsaved edits it replaces the record the editor just committed with older
    /// content, so every other file's resolution silently describes text the user is not looking at
    /// until the next keystroke puts it back. On a plain save it is merely the same file parsed
    /// twice, once from the buffer and once from disk.
    ///
    /// Only records are skipped. A header's other SIDE EFFECT still runs: dropping the cached lex
    /// is a fact about the header, and it is what makes the skip safe — the next analysis of an
    /// open dependent's buffer reads the new header.
    /// </param>
    public IReadOnlyList<string> Apply(string path, WatchedFileChange change, Func<string, bool>? ownedByEditor = null)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);
        bool ownsChangedFile = ownedByEditor is not null && ownedByEditor(normalized);

        if ( change == WatchedFileChange.Deleted )
        {
            // A deleted file that is still open keeps its record: the buffer outlives the file on
            // disk, and dropping it would break every lookup into a document the user can still see
            // and save back. Closing it is what retires the record.
            if ( !ownsChangedFile )
            {
                _indexer.RemoveFile(normalized, language);
            }

            if ( language == ScriptLanguage.Gsh )
            {
                return ReindexInserters(normalized, ownedByEditor);
            }

            return [];
        }

        // Created or changed.
        if ( language == ScriptLanguage.Gsh )
        {
            // Re-analysing dependents re-reads the header, so drop the stale lexed copy first.
            _indexer.InvalidateGsh(normalized);
            if ( !ownsChangedFile )
            {
                _indexer.IndexFile(normalized);
            }

            return ReindexInserters(normalized, ownedByEditor);
        }

        if ( ownsChangedFile )
        {
            return [];
        }

        _indexer.IndexFile(normalized);
        return [normalized];
    }

    private IReadOnlyList<string> ReindexInserters(string normalizedGshPath, Func<string, bool>? ownedByEditor)
    {
        List<string> touched = [];
        foreach ( string dependent in _database.FilesInserting(normalizedGshPath).ToList() )
        {
            // The same test the changed file's own record gets, for the same reason: this reads
            // DISK, and a dependent that is open may hold unsaved edits the disk does not have.
            // Its record was committed from the buffer moments ago and replacing it here is the
            // clobber the gate exists to prevent. Dropping the header's lex above is what makes
            // skipping safe — the next analysis of that buffer reads the new header.
            if ( ownedByEditor is not null && ownedByEditor(dependent) )
            {
                continue;
            }

            _indexer.IndexFile(dependent);
            touched.Add(dependent);
        }

        return touched;
    }
}
