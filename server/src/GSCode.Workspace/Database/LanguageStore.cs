using System.Collections.Concurrent;
using System.Collections.Immutable;
using GSCode.Core.Symbols;

namespace GSCode.Workspace.Database;

/// <summary>
/// All knowledge for ONE language world (GSC or CSC): the path-keyed record map and its
/// reference index. GSC/CSC isolation is structural — two instances, never a filter.
/// Record swaps are atomic; the only lock guards the per-file reference-index diff.
/// </summary>
public sealed class LanguageStore
{
    private readonly ConcurrentDictionary<string, ScriptRecord> _records = new(StringComparer.Ordinal);
    private readonly ReferenceIndex _referenceIndex = new();
    private readonly DeclarationIndex _declarationIndex = new();
    private readonly ClassGraph _classGraph = new();

    /// <summary>
    /// Serialises WRITES so a record swap and the two index diffs that describe it land together.
    /// Indexing runs <c>Parallel.ForEachAsync</c>, so without this the read-previous and the swap
    /// below were separate steps: two upserts of the same file could each read the same previous
    /// record and diff against it, leaving the indexes describing a version neither of them wrote.
    ///
    /// Reads do not take this — the record map is concurrent, and each index snapshots under its
    /// own lock. That is also why the ordering is safe: this gate is only ever taken on the way IN
    /// to an index, never from one.
    /// </summary>
    private readonly Lock _writeGate = new();

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
        lock ( _writeGate )
        {
            _records.TryGetValue(record.Path, out ScriptRecord? previous);
            _records[record.Path] = record;
            _referenceIndex.Apply(record.Path, previous?.References ?? [], record.References);
            _declarationIndex.Apply(record.Path, previous?.Functions ?? [], record.Functions);
            _classGraph.Apply(record.Path, record.Classes);
        }
    }

    /// <summary>Removes a file entirely (deleted from disk).</summary>
    public void Remove(string normalizedPath)
    {
        lock ( _writeGate )
        {
            if ( _records.TryRemove(normalizedPath, out ScriptRecord? previous) )
            {
                _referenceIndex.Apply(normalizedPath, previous.References, []);
                _declarationIndex.Apply(normalizedPath, previous.Functions, []);
                _classGraph.Remove(normalizedPath);
            }
        }
    }

    /// <summary>Paths of the files DECLARING a function key name — see <see cref="DeclarationIndex"/>.</summary>
    public ImmutableArray<string> FilesDeclaring(string keyName)
    {
        return _declarationIndex.FilesDeclaring(keyName);
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
