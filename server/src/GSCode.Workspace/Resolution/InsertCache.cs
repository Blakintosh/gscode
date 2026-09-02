using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;

namespace GSCode.Workspace.Resolution;

/// <summary>
/// Lexed <c>#insert</c> headers, shared across every file that inserts them.
///
/// Without this, <see cref="ResolverInsertProvider"/> re-read and re-lexed a header on EVERY call,
/// and a provider is built per file. BO3's scripts carry 2,137 insert directives naming just 114
/// distinct headers across 814 files, so each header was lexed about 19 times over a full index. It
/// showed up as a fixed per-file cost: BO3's small scripts ran at 3.0-4.4 ms/KB against 0.34-0.62
/// for its large ones, and its median file took 2.38 ms where CoD4 - which has no #insert at all -
/// took 0.26 ms.
/// </summary>
/// <remarks>
/// <para>
/// Keyed by the RESOLVED absolute path, never by the path as written. The written path is precisely
/// the ambiguous one: <c>scripts\shared\shared.gsh</c> means the mod's copy when a mod file asks and
/// raw's copy when a raw file asks, because a mod overlays raw. Keying on it would let whichever
/// file asked first decide the contents for everyone, so a macro edited in a mod would silently
/// change what raw scripts see. The resolved path is unique per physical file, which separates the
/// two by construction rather than by remembering to add the context to the key.
/// </para>
/// <para>
/// Validated by last-write time rather than by an invalidation message. A watcher that misses an
/// event leaves a stale header behind, and a stale header is worse than a slow one: it silently
/// changes what macros expand to, with no error to trace back. A timestamp check is one stat call
/// against a read plus a lex, and it is self-correcting whatever else goes wrong.
/// </para>
/// </remarks>
public sealed class InsertCache : IHeaderMacroCache
{
    private sealed record Entry(InsertedFile File, DateTime LastWriteUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private long _generation;

    /// <inheritdoc />
    public long Generation
    {
        get { return Interlocked.Read(ref _generation); }
    }

    /// <summary>
    /// Records that a header stopped being what it was. Called from every point that replaces or
    /// drops an entry, so no caller has to remember to announce a change separately from making it.
    /// </summary>
    private void Moved()
    {
        Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// The header at this resolved path, lexed once and reused until the file changes. Null when it
    /// could not be read — and a failure is NOT cached, so a header that appears later, or becomes
    /// readable again, is picked up rather than remembered as missing for the session.
    /// </summary>
    public InsertedFile? GetOrAdd(string resolvedPath, IFileSystem fileSystem, Func<InsertedFile?> read)
    {
        DateTime stamp = fileSystem.GetLastWriteTimeUtc(resolvedPath);

        if ( _entries.TryGetValue(resolvedPath, out Entry? cached) && cached.LastWriteUtc == stamp )
        {
            return cached.File;
        }

        InsertedFile? file = read();
        if ( file is null )
        {
            return null;
        }

        // The file moved, so whatever it used to contribute is no longer what it contributes.
        _contributions.TryRemove(resolvedPath, out _);

        // Only a REPLACEMENT is a change; the first read of a header is not, and counting it would
        // make every file analysed during indexing invalidate every other one's parse.
        if ( cached is not null )
        {
            Moved();
        }

        _entries[resolvedPath] = new Entry(file, stamp);
        return file;
    }

    /// <summary>
    /// Offers a header the caller has already read and lexed, if nothing holds one yet.
    ///
    /// A <c>.gsh</c> is an index target in its own right AND an insert source, and those two paths
    /// each read and lexed it independently. The indexer's analysis of the header produces exactly
    /// what <see cref="GetOrAdd"/> would go on to build from scratch, so it is offered here instead.
    ///
    /// Offered rather than assigned, because the race is real and unordered: a <c>.gsc</c> that
    /// inserts this header may be processed first and fill the entry itself. Whoever arrives first
    /// wins, and the two would produce identical content anyway - same file, same lexer, same
    /// profile. So this halves the header work rather than eliminating it, and it never discards a
    /// contribution already walked against an entry that is equally current.
    ///
    /// <paramref name="lastWriteUtc"/> must be read BEFORE the content it describes. Taken after,
    /// a write landing between the two would be stamped as already seen, and the entry would stay
    /// stale until the file changed again.
    /// </summary>
    public void SeedIfAbsent(string resolvedPath, InsertedFile file, DateTime lastWriteUtc)
    {
        _entries.TryAdd(resolvedPath, new Entry(file, lastWriteUtc));
    }

    /// <summary>
    /// What a header CONTRIBUTES once walked - the macros it defines and the insert edges it
    /// carries - so the next file inserting it need not walk it again. Kept beside the lexed
    /// tokens because it is the same header, the same key and the same lifetime; a header whose
    /// timestamp moved drops both together.
    /// </summary>
    private readonly ConcurrentDictionary<string, HeaderContribution> _contributions =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryGet(string resolvedPath, out HeaderContribution contribution)
    {
        return _contributions.TryGetValue(resolvedPath, out contribution!);
    }

    public void Store(string resolvedPath, HeaderContribution contribution)
    {
        _contributions[resolvedPath] = contribution;
    }

    /// <summary>Drops one header, for a caller that knows it changed and will not wait for the stat.</summary>
    public void Invalidate(string resolvedPath)
    {
        bool held = _entries.TryRemove(resolvedPath, out _);
        held |= _contributions.TryRemove(resolvedPath, out _);
        held |= DropContributionsIncluding(resolvedPath);

        if ( held )
        {
            Moved();
        }
    }

    /// <summary>
    /// Drops the stored contribution of every header that reaches this one through its own nested
    /// <c>#insert</c>s, however many hops away.
    ///
    /// A contribution is what the WALK left behind, and the walk of an outer header descends into
    /// the ones it inserts — "a definition recorded inside this header also belongs to whatever
    /// header inserted it", as the preprocessor puts it while adding them. So a wrapper's entry
    /// carries copies of the macros the header underneath it defined, frozen at the moment it was
    /// walked. Dropping the inner header alone left those copies standing, and every file inserting
    /// the wrapper went on replaying values the inner header no longer holds — not until a
    /// re-parse, which replays them again, but until the session ended.
    ///
    /// The reverse edges come from the contributions themselves: each records the nested inserts it
    /// carries, so no separate graph has to be built or kept in step. Only contributions go — an
    /// ancestor's lexed TOKENS are still its own bytes, which have not changed.
    /// </summary>
    private bool DropContributionsIncluding(string resolvedPath)
    {
        HashSet<string> dropped = new(StringComparer.OrdinalIgnoreCase) { resolvedPath };
        bool any = false;

        bool grew = true;
        while ( grew )
        {
            grew = false;
            foreach ( KeyValuePair<string, HeaderContribution> held in _contributions )
            {
                if ( dropped.Contains(held.Key) || !Inserts(held.Value, dropped) )
                {
                    continue;
                }

                _contributions.TryRemove(held.Key, out _);
                dropped.Add(held.Key);
                any = true;
                grew = true;
            }
        }

        return any;
    }

    /// <summary>Whether a contribution's nested inserts name any of the given headers.</summary>
    private static bool Inserts(HeaderContribution contribution, HashSet<string> headers)
    {
        foreach ( InsertEdge nested in contribution.Inserts )
        {
            if ( nested.ResolvedPath is not null && headers.Contains(nested.ResolvedPath) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records that a header appeared or vanished, which changes what an insert path resolves to
    /// without changing any header this holds. <see cref="Invalidate"/> deliberately says nothing
    /// in that case — a header nobody has read cannot be in anyone's parse — and the file that has
    /// been waiting for this one to exist is precisely the file it says nothing about.
    /// </summary>
    public void NoteHeaderSetChanged()
    {
        Moved();
    }

    // Clear() was here, said it was "used when the resolution roots change", and had no caller in
    // src or tests. Its premise was wrong as well as unused: entries are keyed by the RESOLVED
    // absolute path, so a root change cannot make a key mean a different file, and the nested
    // edges a contribution carries are re-resolved on every hit and refused when any lands
    // elsewhere. The roots-changed path needs nothing here, which is why nothing ever called it.

    /// <summary>How many headers are held. For diagnostics and tests.</summary>
    public int Count
    {
        get { return _entries.Count; }
    }
}
