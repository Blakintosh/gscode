using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core.Diagnostics;
using GSCode.Core.Instrumentation;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// All knowledge for ONE language world (GSC or CSC): the path-keyed record map and its
/// reference index. GSC/CSC isolation is structural — two instances, never a filter.
/// Record swaps are atomic; writes to one path are serialised against each other and against
/// nothing else.
/// </summary>
public sealed class LanguageStore
{
    private readonly ConcurrentDictionary<string, ScriptRecord> _records = new(StringComparer.Ordinal);
    private readonly ReferenceIndex _referenceIndex = new();
    private readonly DeclarationIndex _declarationIndex = new();
    private readonly NamespaceIndex _namespaceIndex = new();
    private readonly ClassGraph _classGraph = new();
    private readonly RelativePathIndex _relativePathIndex = new();
    private readonly DependentsIndex _dependents = new();
    private readonly DirectiveIndex _directives = new();
    private readonly PathTreeIndex _pathTree = new(keepExtension: false);
    private readonly VocabularyIndex _vocabulary = new();

    /// <summary>
    /// How many non-raw (mod/workspace) records currently sit at each script-relative path, broken
    /// down by WHICH context — see <see cref="HasOverlayAt"/>. The context matters as much as the
    /// count: mod_a's overlay of a file must shadow that file only when asked FROM mod_a, never from
    /// mod_b (siblings are isolated) or from raw itself (raw loads its own copy regardless of what
    /// any mod does), so a bare "does an overlay exist anywhere" would shadow raw's view of its own
    /// file the moment an unrelated mod happened to touch the same relative path.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _overlayContextsByRelativePath =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Serialises writes TO ONE PATH so a record swap and the index diffs that describe it land
    /// together. Indexing runs <c>Parallel.ForEachAsync</c>, so without this the read-previous and
    /// the swap below are separate steps: two upserts of the same file could each read the same
    /// previous record and diff against it, leaving the indexes describing a version neither of them
    /// wrote.
    ///
    /// One gate for the whole store used to do that, and it was the wrong shape. The race it exists
    /// to stop is between two writers of the SAME file; two writers of different files share
    /// nothing here, because each index below serialises its own dictionary under its own lock. So a
    /// process-wide gate serialised every index diff in the workspace against every other one, and
    /// on CoD4 that made <c>commit.upsert</c> 28.6% of cold-index thread-time at 20x parallelism.
    ///
    /// Striped by path instead. Two upserts of one file still collide, which is the entire contract;
    /// two upserts of different files collide only on a hash coincidence, at which point they are
    /// still correct and merely serialised.
    ///
    /// Reads take none of these — the record map is concurrent and each index snapshots under its
    /// own lock. That is also why the ordering is safe: a gate here is only ever taken on the way IN
    /// to an index, never from one.
    /// </summary>
    private readonly Lock[] _writeGates = CreateGates();

    /// <summary>
    /// Enough stripes that a hash collision between two files being committed at once is rare at
    /// any realistic core count, and small enough to stay a fixed cost. 64 locks is a few kilobytes.
    /// </summary>
    private const int WriteGateCount = 64;

    private static Lock[] CreateGates()
    {
        Lock[] gates = new Lock[WriteGateCount];
        for ( int index = 0; index < gates.Length; index++ )
        {
            gates[index] = new Lock();
        }

        return gates;
    }

    /// <summary>
    /// The gate covering one path. Ordinal, because every path reaching this store has already been
    /// normalised and the record map is keyed the same way — two spellings of one file would be two
    /// records long before they were two gates.
    /// </summary>
    private Lock GateFor(string path)
    {
        int hash = StringComparer.Ordinal.GetHashCode(path);
        return _writeGates[(hash & int.MaxValue) % WriteGateCount];
    }

    /// <summary>Number of files currently held.</summary>
    public int Count
    {
        get { return _records.Count; }
    }

    /// <summary>Every current record (a snapshot enumeration; safe during writes).</summary>
    public IEnumerable<ScriptRecord> AllRecords
    {
        get { return _records.Values; }
    }

    public bool TryGet(string normalizedPath, out ScriptRecord record)
    {
        return _records.TryGetValue(normalizedPath, out record!);
    }

    /// <summary>
    /// What one record contributes to every index here, built from the INCOMING record alone.
    ///
    /// It exists to be built OUTSIDE the write gate. Each of these is O(symbols in the file) —
    /// thousands of references for a large script — and none of them depends on what the store
    /// currently holds, so serialising them made every indexing thread wait on every other thread's
    /// hashing, which was 22% of CoD4's cold-index thread-time at 21x parallelism. The gate covers
    /// only the swap and the dictionary mutations it orders.
    /// </summary>
    private sealed class Contributions
    {
        /// <summary>What a file contributes once it is gone: nothing, in every index.</summary>
        public static Contributions None { get; } = new(
            [], DeclarationIndex.DeclaredKeys.None, [], null, [],
            DirectiveIndex.Contribution.None, VocabularyIndex.Contribution.None);

        private Contributions(
            HashSet<SymbolKey> referenceKeys,
            DeclarationIndex.DeclaredKeys declaredNames,
            HashSet<string> namespaces,
            string? relativeKey,
            HashSet<string> dependencyKeys,
            DirectiveIndex.Contribution directives,
            VocabularyIndex.Contribution vocabulary)
        {
            ReferenceKeys = referenceKeys;
            DeclaredNames = declaredNames;
            Namespaces = namespaces;
            RelativeKey = relativeKey;
            DependencyKeys = dependencyKeys;
            Directives = directives;
            Vocabulary = vocabulary;
        }

        public HashSet<SymbolKey> ReferenceKeys { get; }

        public DeclarationIndex.DeclaredKeys DeclaredNames { get; }

        public HashSet<string> Namespaces { get; }

        public string? RelativeKey { get; }

        public HashSet<string> DependencyKeys { get; }

        public DirectiveIndex.Contribution Directives { get; }

        public VocabularyIndex.Contribution Vocabulary { get; }

        public static Contributions Of(ScriptRecord record)
        {
            return new Contributions(
                ReferenceIndex.KeysOf(record.References),
                DeclarationIndex.KeysOf(record),
                NamespaceIndex.NamespacesOf(record.Functions),
                RelativePathIndex.KeyOf(record),
                DependentsIndex.KeysOf(record),
                DirectiveIndex.Of(record),
                VocabularyIndex.Of(record));
        }
    }

    /// <summary>
    /// Replaces one file's contribution to EVERY index, under the caller's gate.
    ///
    /// The one place the index list is written out. <see cref="Upsert"/> and <see cref="Remove"/>
    /// each named all nine for themselves, so adding a tenth index was two edits and forgetting the
    /// removal half left a deleted file's keys in the index with nothing to report it. A removal is
    /// an upsert whose contribution is <see cref="Contributions.None"/> and whose next record is
    /// null, which is what every index's own diff already means by an empty new set.
    ///
    /// The `PerfTracker` scopes therefore cover removals too, where they used to cover upserts
    /// alone. Nothing in `PERF.md`'s table moves: it measures cold-index thread-time, and a cold
    /// index removes nothing.
    /// </summary>
    /// <param name="previous">
    /// The record being replaced, readable only under the gate — reading it outside would let two
    /// upserts of one file diff against the same version. Null on a cold index, where every OLD set
    /// below is empty.
    /// </param>
    private void ApplyIndexes(string path, ScriptRecord? previous, ScriptRecord? next, Contributions contributions)
    {
        PerfTracker.Begin("upsert.reference");
        _referenceIndex.Apply(path, ReferenceIndex.KeysOf(previous?.References ?? []), contributions.ReferenceKeys);
        PerfTracker.End();

        PerfTracker.Begin("upsert.declaration");
        _declarationIndex.Apply(path, DeclarationIndex.KeysOf(previous), contributions.DeclaredNames);
        PerfTracker.End();

        PerfTracker.Begin("upsert.namespace");
        _namespaceIndex.Apply(path, NamespaceIndex.NamespacesOf(previous?.Functions ?? []), contributions.Namespaces);
        PerfTracker.End();

        PerfTracker.Begin("upsert.class");
        // The graph reads its own previous contribution, so an empty `next` IS its removal.
        _classGraph.Apply(path, next?.Classes ?? []);
        PerfTracker.End();

        _relativePathIndex.Apply(path, RelativePathIndex.KeyOf(previous), contributions.RelativeKey);
        _dependents.Apply(path, DependentsIndex.KeysOf(previous), contributions.DependencyKeys);
        _directives.Apply(path, DirectiveIndex.Of(previous), contributions.Directives);
        _pathTree.Apply(previous, next);

        PerfTracker.Begin("upsert.vocabulary");
        _vocabulary.Apply(path, VocabularyIndex.Of(previous), contributions.Vocabulary);
        PerfTracker.End();
    }

    /// <summary>Swaps in a new record and diffs it into every index.</summary>
    public void Upsert(ScriptRecord record)
    {
        // Built BEFORE the gate — see Contributions for what that is worth.
        Contributions contributions = Contributions.Of(record);

        lock ( GateFor(record.Path) )
        {
            _records.TryGetValue(record.Path, out ScriptRecord? previous);
            _records[record.Path] = record;
            AdjustOverlayCount(previous, record);

            ApplyIndexes(record.Path, previous, record, contributions);
        }
    }

    /// <summary>
    /// Swaps a record's diagnostics in place — for the full-mode lint sweep, which changes what a
    /// CLOSED record REPORTS without changing what it declares or references. Skips every index
    /// diff <see cref="Upsert"/> does, since none of them can have moved: a lint sweep re-analyses
    /// the same content, not new content.
    ///
    /// Gated on <paramref name="expectedContentHash"/> rather than applied unconditionally: the
    /// sweep reads the file, re-parses it and runs the lints — all of that off the write gate — so
    /// by the time it comes back with an answer, an ordinary edit (a save, a watched-file change)
    /// could have replaced this record with one describing DIFFERENT content. Applying the
    /// sweep's diagnostics onto that newer record would describe content the record no longer
    /// has. A hash mismatch means someone else now owns this path's diagnostics — the watched-file
    /// path, an open document, or a later sweep — so this quietly declines rather than guessing.
    /// </summary>
    /// <returns>False when the record moved or vanished before this could apply.</returns>
    public bool SetDiagnostics(string normalizedPath, ulong expectedContentHash, ImmutableArray<Diagnostic> diagnostics)
    {
        lock ( GateFor(normalizedPath) )
        {
            if ( !_records.TryGetValue(normalizedPath, out ScriptRecord? existing)
                || existing.ContentHash != expectedContentHash )
            {
                return false;
            }

            _records[normalizedPath] = existing with { Diagnostics = diagnostics };
            return true;
        }
    }

    /// <summary>Removes a file entirely (deleted from disk).</summary>
    public void Remove(string normalizedPath)
    {
        lock ( GateFor(normalizedPath) )
        {
            if ( _records.TryRemove(normalizedPath, out ScriptRecord? previous) )
            {
                AdjustOverlayCount(previous, next: null);
                ApplyIndexes(normalizedPath, previous, next: null, Contributions.None);
            }
        }
    }

    /// <summary>Keeps <see cref="_overlayContextsByRelativePath"/> correct across an upsert or removal.</summary>
    private void AdjustOverlayCount(ScriptRecord? previous, ScriptRecord? next)
    {
        if ( previous is not null && previous.ContextId != "raw" && previous.RelativePath.Length > 0 )
        {
            AdjustOverlayCount(previous.RelativePath, previous.ContextId, -1);
        }

        if ( next is not null && next.ContextId != "raw" && next.RelativePath.Length > 0 )
        {
            AdjustOverlayCount(next.RelativePath, next.ContextId, 1);
        }
    }

    private void AdjustOverlayCount(string relativePath, string contextId, int delta)
    {
        ConcurrentDictionary<string, int> byContext = _overlayContextsByRelativePath.GetOrAdd(
            relativePath, static _ => new ConcurrentDictionary<string, int>(StringComparer.Ordinal));

        byContext.AddOrUpdate(contextId, delta, (_, count) => count + delta);
    }

    /// <summary>
    /// Whether a mod or workspace copy VISIBLE TO <paramref name="askingContextId"/> currently sits
    /// at this script-relative path — the engine loads ONLY that copy, replacing the raw one
    /// wholesale, whatever the two copies individually declare. See
    /// <see cref="DatabaseQueries.ApplyShadowing"/>, the caller this exists for.
    ///
    /// Visibility is <see cref="ScriptDatabase.CanSee"/>'s, asked of each context that has an
    /// overlay here: mod_a's own overlay counts when mod_a is asking, mod_b's does not (siblings
    /// are isolated), and raw asking sees no overlay at all, since <c>CanSee("raw", "mod:x")</c> is
    /// false — raw loads its own file regardless of what any mod does with the same relative path.
    /// </summary>
    public bool HasOverlayAt(string relativePath, string askingContextId)
    {
        if ( relativePath.Length == 0
            || !_overlayContextsByRelativePath.TryGetValue(relativePath, out ConcurrentDictionary<string, int>? byContext) )
        {
            return false;
        }

        foreach ( KeyValuePair<string, int> entry in byContext )
        {
            if ( entry.Value > 0 && ScriptDatabase.CanSee(askingContextId, entry.Key) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Paths of the files DECLARING a function key name — see <see cref="DeclarationIndex"/>.</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        return _declarationIndex.FilesDeclaring(keyName);
    }

    /// <summary>
    /// Whether any file declares a function or method of this name inside a dev block — see
    /// <see cref="DeclarationIndex.MayBeDevOnly"/>.
    /// </summary>
    public bool MayBeDevOnly(string keyName)
    {
        return _declarationIndex.MayBeDevOnly(keyName);
    }

    /// <summary>Paths of the files declaring a function key name INTO one namespace.</summary>
    public ImmutableArray<string> FilesDeclaring(string namespaceName, string keyName)
    {
        return _declarationIndex.FilesDeclaring(namespaceName, keyName);
    }

    /// <summary>
    /// Paths of the files declaring a function INTO a namespace — see <see cref="NamespaceIndex"/>.
    /// </summary>
    public ImmutableArray<string> FilesDeclaringInto(string namespaceName)
    {
        return _namespaceIndex.FilesDeclaringInto(namespaceName);
    }

    /// <summary>
    /// Paths of the files at a script-relative path, in <see cref="RelativePathIndex.Normalize"/>'s
    /// form — see <see cref="RelativePathIndex"/>.
    /// </summary>
    public ImmutableArray<string> FilesAt(string normalizedScriptPath)
    {
        return _relativePathIndex.FilesAt(normalizedScriptPath);
    }

    /// <summary>
    /// Paths of the files naming a script-relative path through an import edge or a path call —
    /// see <see cref="DependentsIndex"/>.
    /// </summary>
    public ImmutableArray<string> FilesNaming(string normalizedScriptPath)
    {
        return _dependents.FilesNaming(normalizedScriptPath);
    }

    /// <summary>
    /// Paths of the files writing a directive path, in <see cref="DirectiveIndex.WrittenKey"/>'s
    /// form — see <see cref="DirectiveIndex"/>.
    /// </summary>
    public ImmutableArray<string> FilesWriting(string writtenKey)
    {
        return _directives.FilesWriting(writtenKey);
    }

    /// <summary>Paths of the files inserting the header that resolved to this path — see <see cref="DirectiveIndex"/>.</summary>
    public ImmutableArray<string> FilesInserting(string resolvedHeaderPath)
    {
        return _directives.FilesInserting(resolvedHeaderPath);
    }

    /// <summary>
    /// The segments directly under a script-relative folder that a file in
    /// <paramref name="askingContextId"/> can see — see <see cref="PathTreeIndex"/>.
    /// </summary>
    public List<(string Segment, bool IsFolder)> PathChildren(string directory, string askingContextId)
    {
        return _pathTree.Children(directory, askingContextId);
    }

    /// <summary>
    /// The distinct literals of one kind used in a file <paramref name="askingContextId"/> can see —
    /// see <see cref="VocabularyIndex"/>.
    /// </summary>
    public List<string> VisibleLiterals(SymbolKind kind, string askingContextId)
    {
        return _vocabulary.Literals(kind, path => IsVisibleTo(path, askingContextId));
    }

    /// <summary>
    /// The distinct field names assigned in a file <paramref name="askingContextId"/> can see, on one
    /// owner when <paramref name="ownerName"/> is given — see <see cref="VocabularyIndex"/>.
    /// </summary>
    public List<string> VisibleFieldNames(string? ownerName, string askingContextId)
    {
        return _vocabulary.FieldNames(ownerName, path => IsVisibleTo(path, askingContextId));
    }

    /// <summary>
    /// The function names beginning with <paramref name="lowercasePrefix"/> that
    /// <paramref name="askingContextId"/> can see — see <see cref="DeclarationIndex"/>.
    /// </summary>
    public List<string> VisibleDeclaredNames(string lowercasePrefix, string askingContextId)
    {
        return _declarationIndex.NamesStartingWith(lowercasePrefix, path => IsVisibleTo(path, askingContextId));
    }

    private bool IsVisibleTo(string path, string askingContextId)
    {
        return _records.TryGetValue(path, out ScriptRecord? record)
            && ScriptDatabase.CanSee(askingContextId, record.ContextId);
    }

    /// <summary>Paths of every file that mentions the key (definition sites included).</summary>
    public ImmutableArray<string> FilesReferencing(SymbolKey key)
    {
        return _referenceIndex.FilesFor(key);
    }

    /// <summary>Every function key in this store sharing a bare name. See <see cref="ReferenceIndex.KeysNamed"/>.</summary>
    public List<SymbolKey> ReferenceKeysNamed(string name)
    {
        return _referenceIndex.KeysNamed(name);
    }

    /// <summary>Class declarations and inheritance for this language world.</summary>
    public ClassGraph Classes
    {
        get { return _classGraph; }
    }
}
