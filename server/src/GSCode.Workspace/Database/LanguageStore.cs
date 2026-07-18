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

    /// <summary>Swaps in a new record and diffs its references into the index.</summary>
    public void Upsert(ScriptRecord record)
    {
        _records.TryGetValue(record.Path, out ScriptRecord? previous);
        _records[record.Path] = record;
        _referenceIndex.Apply(record.Path, previous?.References ?? [], record.References);
    }

    /// <summary>Removes a file entirely (deleted from disk).</summary>
    public void Remove(string normalizedPath)
    {
        if ( _records.TryRemove(normalizedPath, out ScriptRecord? previous) )
        {
            _referenceIndex.Apply(normalizedPath, previous.References, []);
        }
    }

    /// <summary>Paths of every file that mentions the key (definition sites included).</summary>
    public ImmutableArray<string> FilesReferencing(SymbolKey key)
    {
        return _referenceIndex.FilesFor(key);
    }
}
