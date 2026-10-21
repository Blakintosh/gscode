using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using GSCode.Core.Symbols;
using GSCode.Core.Text;

namespace GSCode.Workspace.Database;

/// <summary>
/// One record's references, held compactly and grouped by key.
///
/// A <see cref="ReferenceEntry"/> is 56 bytes — a <see cref="SymbolKey"/> of three references and an
/// enum, a range of four ints, the kind and a flag — and a heavy BO3 script has about 1,550 of them,
/// written against a few hundred distinct keys. Across 50,000 such files the references were 113
/// million entries, 6.3 GB, half of everything the index retained (PERF.md, the scale section). Here
/// each distinct key is stored once and an entry is 16 bytes: the key's index, the range packed into
/// a start line, a line span and two 16-bit columns, the kind and the flag. A file whose ranges do not
/// fit those widths keeps its entries as they were, so nothing is ever lost.
///
/// It also answers "this key's entries in this file" without reading the rest. The reference query
/// reads that for every file mentioning a key, and walking each one's whole list to keep the few
/// that match was most of the time find-references and CodeLens took at scale. Entries are listed
/// by key — each key's in source order — beside the source-order list, and keys are found through
/// their hash codes, sorted. Those hash codes are this process's own and never leave it.
///
/// Iterating the table yields the entries in SOURCE order, exactly as the array it replaces did, so
/// every caller that walks all of them sees the same sequence.
/// </summary>
[CollectionBuilder(typeof(ReferenceTable), nameof(Create))]
public sealed class ReferenceTable : IReadOnlyList<ReferenceEntry>
{
    /// <summary>One entry, 16 bytes. <see cref="LineSpan"/> is the end line less the start line.</summary>
    private struct Packed
    {
        public int Key;
        public int StartLine;
        public ushort StartCharacter;
        public ushort LineSpan;
        public ushort EndCharacter;
        public byte Kind;
        public bool FromMacro;
    }

    public static ReferenceTable Empty { get; } = Create([]);

    private readonly SymbolKey[] _keys;
    private readonly Packed[] _packed;

    /// <summary>The entries as given, when any of them does not fit <see cref="Packed"/>; else null.</summary>
    private readonly ReferenceEntry[]? _wide;

    /// <summary>Entry positions grouped by key index, each group in source order.</summary>
    private readonly int[] _byKey;

    /// <summary>Where each key's group starts in <see cref="_byKey"/>; one longer than the keys.</summary>
    private readonly int[] _keyStart;

    /// <summary>Every key's hash code, sorted, and the key each belongs to.</summary>
    private readonly int[] _hashes;
    private readonly int[] _hashKeys;

    private ReferenceTable(
        SymbolKey[] keys, Packed[] packed, ReferenceEntry[]? wide, int[] byKey, int[] keyStart, int[] hashes, int[] hashKeys)
    {
        _keys = keys;
        _packed = packed;
        _wide = wide;
        _byKey = byKey;
        _keyStart = keyStart;
        _hashes = hashes;
        _hashKeys = hashKeys;
    }

    public static ReferenceTable From(ImmutableArray<ReferenceEntry> entries)
    {
        return Create(entries.AsSpan());
    }

    public static ReferenceTable Create(ReadOnlySpan<ReferenceEntry> entries)
    {
        Dictionary<SymbolKey, int> indexOf = [];
        List<SymbolKey> keys = [];
        int[] keyOf = new int[entries.Length];
        bool packable = true;

        for ( int index = 0; index < entries.Length; index++ )
        {
            ReferenceEntry entry = entries[index];
            if ( !indexOf.TryGetValue(entry.Key, out int key) )
            {
                key = keys.Count;
                indexOf[entry.Key] = key;
                keys.Add(entry.Key);
            }

            keyOf[index] = key;
            packable = packable && Fits(entry);
        }

        Packed[] packed = [];
        ReferenceEntry[]? wide = null;
        if ( packable )
        {
            packed = new Packed[entries.Length];
            for ( int index = 0; index < entries.Length; index++ )
            {
                ReferenceEntry entry = entries[index];
                packed[index] = new Packed
                {
                    Key = keyOf[index],
                    StartLine = entry.Range.Start.Line,
                    StartCharacter = (ushort)entry.Range.Start.Character,
                    LineSpan = (ushort)(entry.Range.End.Line - entry.Range.Start.Line),
                    EndCharacter = (ushort)entry.Range.End.Character,
                    Kind = (byte)entry.Kind,
                    FromMacro = entry.FromMacro,
                };
            }
        }
        else
        {
            wide = entries.ToArray();
        }

        // Group by key with a counting sort, which keeps each key's entries in source order.
        int[] keyStart = new int[keys.Count + 1];
        foreach ( int key in keyOf )
        {
            keyStart[key + 1]++;
        }

        for ( int key = 0; key < keys.Count; key++ )
        {
            keyStart[key + 1] += keyStart[key];
        }

        int[] byKey = new int[entries.Length];
        int[] next = new int[keys.Count];
        Array.Copy(keyStart, next, keys.Count);
        for ( int index = 0; index < keyOf.Length; index++ )
        {
            byKey[next[keyOf[index]]++] = index;
        }

        int[] hashes = new int[keys.Count];
        int[] hashKeys = new int[keys.Count];
        for ( int key = 0; key < keys.Count; key++ )
        {
            hashes[key] = keys[key].GetHashCode();
            hashKeys[key] = key;
        }

        Array.Sort(hashes, hashKeys);
        return new ReferenceTable([.. keys], packed, wide, byKey, keyStart, hashes, hashKeys);
    }

    private static bool Fits(ReferenceEntry entry)
    {
        TextRange range = entry.Range;
        int span = range.End.Line - range.Start.Line;
        return range.Start.Line >= 0
            && range.Start.Character is >= 0 and <= ushort.MaxValue
            && range.End.Character is >= 0 and <= ushort.MaxValue
            && span is >= 0 and <= ushort.MaxValue
            && (int)entry.Kind is >= 0 and <= byte.MaxValue;
    }

    public int Count
    {
        get { return _wide?.Length ?? _packed.Length; }
    }

    /// <summary>The same as <see cref="Count"/>, under the name the array it replaces used.</summary>
    public int Length
    {
        get { return Count; }
    }

    public bool IsEmpty
    {
        get { return Count == 0; }
    }

    /// <summary>Every distinct key, each once.</summary>
    public ReadOnlySpan<SymbolKey> Keys
    {
        get { return _keys; }
    }

    public ReferenceEntry this[int index]
    {
        get
        {
            if ( _wide is not null )
            {
                return _wide[index];
            }

            Packed packed = _packed[index];
            return new ReferenceEntry(
                _keys[packed.Key],
                new TextRange(
                    new Position(packed.StartLine, packed.StartCharacter),
                    new Position(packed.StartLine + packed.LineSpan, packed.EndCharacter)),
                (ReferenceKind)packed.Kind,
                packed.FromMacro);
        }
    }

    /// <summary>Whether any entry has this key.</summary>
    public bool Contains(SymbolKey key)
    {
        return IndexOf(key) >= 0;
    }

    /// <summary>This key's entries, in source order, without reading any other key's.</summary>
    public KeyEntries EntriesFor(SymbolKey key)
    {
        int index = IndexOf(key);
        return index < 0 ? default : new KeyEntries(this, _keyStart[index], _keyStart[index + 1]);
    }

    private int IndexOf(SymbolKey key)
    {
        int hash = key.GetHashCode();
        int found = Array.BinarySearch(_hashes, hash);
        if ( found < 0 )
        {
            return -1;
        }

        // Several keys can share a hash code: step back to the first, then test each in the run.
        while ( found > 0 && _hashes[found - 1] == hash )
        {
            found--;
        }

        for ( ; found < _hashes.Length && _hashes[found] == hash; found++ )
        {
            int candidate = _hashKeys[found];
            if ( _keys[candidate] == key )
            {
                return candidate;
            }
        }

        return -1;
    }

    public Enumerator GetEnumerator()
    {
        return new Enumerator(this);
    }

    IEnumerator<ReferenceEntry> IEnumerable<ReferenceEntry>.GetEnumerator()
    {
        return GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>The entries in source order.</summary>
    public struct Enumerator : IEnumerator<ReferenceEntry>
    {
        private readonly ReferenceTable _table;
        private int _index;

        internal Enumerator(ReferenceTable table)
        {
            _table = table;
            _index = -1;
        }

        public readonly ReferenceEntry Current
        {
            get { return _table[_index]; }
        }

        readonly object IEnumerator.Current
        {
            get { return Current; }
        }

        public bool MoveNext()
        {
            return ++_index < _table.Count;
        }

        public void Reset()
        {
            _index = -1;
        }

        public readonly void Dispose()
        {
        }
    }

    /// <summary>One key's entries, in source order.</summary>
    public readonly struct KeyEntries
    {
        private readonly ReferenceTable? _table;
        private readonly int _start;
        private readonly int _end;

        internal KeyEntries(ReferenceTable table, int start, int end)
        {
            _table = table;
            _start = start;
            _end = end;
        }

        public int Count
        {
            get { return _end - _start; }
        }

        public KeyEnumerator GetEnumerator()
        {
            return new KeyEnumerator(_table, _start, _end);
        }
    }

    public struct KeyEnumerator
    {
        private readonly ReferenceTable? _table;
        private readonly int _end;
        private int _position;

        internal KeyEnumerator(ReferenceTable? table, int start, int end)
        {
            _table = table;
            _end = end;
            _position = start - 1;
        }

        public readonly ReferenceEntry Current
        {
            get { return _table![_table._byKey[_position]]; }
        }

        public bool MoveNext()
        {
            return _table is not null && ++_position < _end;
        }
    }
}
