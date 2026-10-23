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
    /// <summary>Files re-indexed between collections when a header save replaces many records at once.</summary>
    private const int ReindexChunk = 6250;

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
    /// <param name="invalidateResolution">
    /// False when the caller applying a batch has already called <see cref="NoteFilesAppearedOrVanished"/>
    /// once for all of it, rather than once per file that appeared or vanished.
    /// </param>
    public IReadOnlyList<string> Apply(
        string path, WatchedFileChange change, Func<string, bool>? ownedByEditor = null, bool invalidateResolution = true)
    {
        List<string> touched = [.. ApplyToFile(path, change, ownedByEditor, invalidateResolution)];

        // Raw shadows devraw at the same relative path, so a raw file appearing retires the devraw
        // copy the index held in its place, and one vanishing brings that copy back. Without this
        // the index keeps answering from whichever existed when it was built until a restart.
        if ( change == WatchedFileChange.Changed )
        {
            return touched;
        }

        string? devRaw = _indexer.DevRawCounterpart(path);
        if ( devRaw is null )
        {
            return touched;
        }

        if ( change == WatchedFileChange.Created )
        {
            if ( _database.TryGetAnyRecord(devRaw, out _) )
            {
                _indexer.RemoveFile(devRaw, ScriptAnalysis.LanguageFromPath(devRaw));
            }
        }
        else if ( ownedByEditor is null || !ownedByEditor(devRaw) )
        {
            _indexer.IndexFile(devRaw);
            touched.Add(devRaw);
        }

        return touched;
    }

    /// <summary>
    /// Whether a created or changed file belongs in the index: one the cold index would visit, or
    /// one it already holds. A script appearing in a folder the walk skips — the game install outside
    /// raw's script folder — stays out, as it would after a restart.
    /// </summary>
    private bool ShouldIndex(string normalizedPath)
    {
        return _indexer.IsIndexTarget(normalizedPath) || _database.TryGetAnyRecord(normalizedPath, out _);
    }

    /// <summary>
    /// Drops every cached "does this exist" answer the resolver has given out, for a batch of changes
    /// in which some file appeared or vanished — what <see cref="Apply"/> does per such file otherwise.
    /// </summary>
    public void NoteFilesAppearedOrVanished()
    {
        _indexer.InvalidateResolutionCache();
    }

    private IReadOnlyList<string> ApplyToFile(
        string path, WatchedFileChange change, Func<string, bool>? ownedByEditor, bool invalidateResolution)
    {
        string normalized = PathUtil.NormalizeAbsolute(path);
        ScriptLanguage language = ScriptAnalysis.LanguageFromPath(normalized);
        bool ownsChangedFile = ownedByEditor is not null && ownedByEditor(normalized);

        // A file appearing or vanishing can falsify any cached "does this exist" answer the
        // resolver has given out — for a .gsc/.csc target as much as for a .gsh one, since
        // #using/#include resolve through the same PathResolver.Resolve the GSH-only
        // NoteHeaderSetChanged below cannot speak for. A plain content change cannot: nothing
        // about whether a target EXISTS moved. See PathResolver.InvalidateResolutionCache.
        if ( change != WatchedFileChange.Changed && invalidateResolution )
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
                // Unconditionally, unlike the record: RemoveFile drops the cache only for a closed file,
                // and a header deleted while open would leave every inserting file expanding a header
                // that is no longer there. Dropping a lexed copy of a missing file is a fact about the
                // header, not about who has it open — the same reason the changed branch drops it
                // before anyone is told.
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
            if ( !ownsChangedFile && ShouldIndex(normalized) )
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

        if ( ownsChangedFile || !ShouldIndex(normalized) )
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
    /// The paths of the closed files inserting a header, skipping any the editor has open. A method of
    /// its own so the list of their RECORDS is never in scope while the re-index replaces them — a
    /// precaution: holding every old record across that would keep the whole index alive twice.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private List<string> PathsToReindex(string normalizedGshPath, string headerRelativePath, Func<string, bool>? ownedByEditor)
    {
        List<string> paths = [];
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

            paths.Add(record.Path);
        }

        return paths;
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
        List<string> toIndex = PathsToReindex(normalizedGshPath, headerRelativePath, ownedByEditor);

        // In parallel, as the cold index analyses: IndexFile is the same per-file step it runs on
        // every core. One after another, saving a header every script inserts — shared.gsh, on BO3 —
        // re-indexed the whole workspace on one thread: 45 s projected at 10,000 heavy files.
        //
        // In chunks of ReindexChunk, with a blocking, non-compacting gen2 collection between them.
        // Every record this replaces has long since reached gen2, so each becomes gen2 garbage the
        // moment its replacement is committed, and gen2 runs on its own schedule. Saving shared.gsh
        // at 50,000 heavy files (49,863 inserters), measured with the heap sampled through the save:
        //   one pass, no collections asked for       46.4 s, 15.2 GB peak (gen2 8.5 -> 13.2 GB)
        //   4,096 a chunk, background gen2 between   41.4 s, 13.9 GB — only 3 of 11 requests ran,
        //                                            the rest landing while one was already going
        //   12,500 a chunk, blocking gen2 between    37.5 s, 12.7 GB
        //   6,250 a chunk, blocking gen2 between     39.3 s, 12.4 GB, against the cold index's 12.2
        // A save then needs no more memory than indexing the workspace did in the first place.
        ParallelOptions options = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
        for ( int start = 0; start < toIndex.Count; start += ReindexChunk )
        {
            int end = Math.Min(start + ReindexChunk, toIndex.Count);
            Parallel.For(start, end, options, index => _indexer.IndexFile(toIndex[index]));

            if ( end < toIndex.Count )
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            }
        }

        return toIndex;
    }
}
