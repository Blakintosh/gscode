using System.Collections.Concurrent;
using System.Collections.Immutable;
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

    /// <summary>Swaps in a new record and diffs it into the reference index and the class graph.</summary>
    public void Upsert(ScriptRecord record)
    {
        // Built BEFORE the gate. Both are O(symbols in the file) — thousands of references for a
        // large script — and they depend only on the INCOMING record, so nothing about them needs
        // to be serialised. Doing them inside meant every indexing thread waited on every other
        // thread's hashing, which made this stage 22% of CoD4's cold-index thread-time at 21x
        // parallelism. The gate now covers only the swap and the dictionary mutations it orders.
        HashSet<SymbolKey> newKeys = ReferenceIndex.KeysOf(record.References);
        HashSet<string> newNames = DeclarationIndex.NamesOf(record.Functions);
        HashSet<string> newNamespaces = NamespaceIndex.NamespacesOf(record.Functions);

        lock ( GateFor(record.Path) )
        {
            _records.TryGetValue(record.Path, out ScriptRecord? previous);
            _records[record.Path] = record;

            // The OLD sets still have to be built here: `previous` is only knowable under the gate,
            // and reading it outside would let two upserts of one file diff against the same
            // version. On a cold index it is always null and these are empty.
            PerfTracker.Begin("upsert.reference");
            _referenceIndex.Apply(record.Path, ReferenceIndex.KeysOf(previous?.References ?? []), newKeys);
            PerfTracker.End();

            PerfTracker.Begin("upsert.declaration");
            _declarationIndex.Apply(record.Path, DeclarationIndex.NamesOf(previous?.Functions ?? []), newNames);
            PerfTracker.End();

            PerfTracker.Begin("upsert.namespace");
            _namespaceIndex.Apply(record.Path, NamespaceIndex.NamespacesOf(previous?.Functions ?? []), newNamespaces);
            PerfTracker.End();

            PerfTracker.Begin("upsert.class");
            _classGraph.Apply(record.Path, record.Classes);
            PerfTracker.End();
        }
    }

    /// <summary>Removes a file entirely (deleted from disk).</summary>
    public void Remove(string normalizedPath)
    {
        lock ( GateFor(normalizedPath) )
        {
            if ( _records.TryRemove(normalizedPath, out ScriptRecord? previous) )
            {
                _referenceIndex.Apply(normalizedPath, ReferenceIndex.KeysOf(previous.References), []);
                _declarationIndex.Apply(normalizedPath, DeclarationIndex.NamesOf(previous.Functions), []);
                _namespaceIndex.Apply(normalizedPath, NamespaceIndex.NamespacesOf(previous.Functions), []);
                _classGraph.Remove(normalizedPath);
            }
        }
    }

    /// <summary>Paths of the files DECLARING a function key name — see <see cref="DeclarationIndex"/>.</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        return _declarationIndex.FilesDeclaring(keyName);
    }

    /// <summary>
    /// Paths of the files declaring a function INTO a namespace — see <see cref="NamespaceIndex"/>.
    /// </summary>
    public ImmutableArray<string> FilesDeclaringInto(string namespaceName)
    {
        return _namespaceIndex.FilesDeclaringInto(namespaceName);
    }

    /// <summary>Paths of every file that mentions the key (definition sites included).</summary>
    public ImmutableArray<string> FilesReferencing(SymbolKey key)
    {
        return _referenceIndex.FilesFor(key);
    }

    /// <summary>Class declarations and inheritance for this language world.</summary>
    public ClassGraph Classes
    {
        get { return _classGraph; }
    }
}
