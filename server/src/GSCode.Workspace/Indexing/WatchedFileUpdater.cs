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

        // A file appearing or vanishing can falsify any cached "does this exist" answer the
        // resolver has given out — for a .gsc/.csc target as much as for a .gsh one, since
        // #using/#include resolve through the same PathResolver.Resolve the GSH-only
        // NoteHeaderSetChanged below cannot speak for. A plain content change cannot: nothing
        // about whether a target EXISTS moved. See PathResolver.InvalidateResolutionCache.
        if ( change != WatchedFileChange.Changed )
        {
            _indexer.InvalidateResolutionCache();
        }

        if ( change == WatchedFileChange.Deleted )
        {
            // Read while the record is still there: it is how a file that inserts this header by
            // its WRITTEN path is recognised, and the removal below takes the answer away.
            string relativePath = HeaderRelativePath(normalized, language);

            // A deleted file that is still open keeps its record: the buffer outlives the file on
            // disk, and dropping it would break every lookup into a document the user can still see
            // and save back. Closing it is what retires the record.
            if ( !ownsChangedFile )
            {
                _indexer.RemoveFile(normalized, language);
            }

            if ( language == ScriptLanguage.Gsh )
            {
                // Unconditionally, unlike the record: the cache is dropped by RemoveFile only when
                // the file is closed, so a header deleted while open left every inserting file
                // expanding a header that is no longer there. Dropping a lexed copy of a file that
                // no longer exists is a fact about the header, not about who has it open — the same
                // reason the changed branch drops it before anyone is told.
                _indexer.InvalidateGsh(normalized);

                // A header vanishing changes what an insert path resolves to for anyone it used to
                // shadow, exactly as a header appearing does, and the drop above announces nothing
                // when nothing was held.
                _indexer.NoteHeaderSetChanged();

                return ReindexInserters(normalized, relativePath, ownedByEditor);
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

            // A header that did not exist a moment ago holds nothing to invalidate, so the drop
            // above announces nothing — yet what an insert path RESOLVES to has just changed for
            // every file that could not resolve it before, and for every file a new mod copy now
            // shadows a raw header for. Their parses expanded the old answer and have to be redone.
            if ( change == WatchedFileChange.Created )
            {
                _indexer.NoteHeaderSetChanged();
            }

            // AFTER the index above, which is what gives a newly created header a record to read
            // its relative path from.
            return ReindexInserters(normalized, HeaderRelativePath(normalized, language), ownedByEditor);
        }

        if ( ownsChangedFile )
        {
            return [];
        }

        _indexer.IndexFile(normalized);
        return [normalized];
    }

    /// <summary>The header's path as a <c>#insert</c> would write it, or "" when it has no record.</summary>
    private string HeaderRelativePath(string normalizedPath, ScriptLanguage language)
    {
        if ( language != ScriptLanguage.Gsh || !_database.TryGetGsh(normalizedPath, out ScriptRecord record) )
        {
            return "";
        }

        return PathUtil.NormalizeScriptPath(record.RelativePath);
    }

    /// <summary>
    /// Re-indexes every closed file whose analysis this header decides — see
    /// <see cref="DatabaseQueries.ScriptsInserting"/> for what "decides" covers — and reports them
    /// for a diagnostics republish.
    /// </summary>
    /// <param name="headerRelativePath">
    /// The header as a directive would write it, or "" to match on resolved paths alone.
    /// </param>
    private IReadOnlyList<string> ReindexInserters(
        string normalizedGshPath, string headerRelativePath, Func<string, bool>? ownedByEditor)
    {
        List<string> touched = [];
        foreach ( ScriptRecord record in DatabaseQueries.ScriptsInserting(_database, normalizedGshPath, headerRelativePath) )
        {
            // The same test the changed file's own record gets, for the same reason: this reads
            // DISK, and a dependent that is open may hold unsaved edits the disk does not have.
            // Its record was committed from the buffer moments ago and replacing it here is the
            // clobber the gate exists to prevent. Dropping the header's lex above is what makes
            // skipping safe — the next analysis of that buffer reads the new header.
            if ( ownedByEditor is not null && ownedByEditor(record.Path) )
            {
                continue;
            }

            _indexer.IndexFile(record.Path);
            touched.Add(record.Path);
        }

        return touched;
    }
}
