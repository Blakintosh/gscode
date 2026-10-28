using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// <see cref="ReferenceTable"/> replaces a plain array of references with a packed, key-grouped one,
/// and every reference query in the workspace now reads it. What can go wrong is an answer that
/// differs from the array's: an entry out of order, a key's entries missing one, a range or flag
/// that did not survive packing, or two keys that share a hash code answering for each other. Each
/// fact below checks one of those against the entries the table was built from.
/// </summary>
public class ReferenceTableTests
{
    private static SymbolKey Function(string name, string? ns = "util")
    {
        return new SymbolKey(ns, name, SymbolKind.Function);
    }

    private static ReferenceEntry At(SymbolKey key, int line, int character, int endLine = -1, int endCharacter = -1,
        ReferenceKind kind = ReferenceKind.Call, bool fromMacro = false)
    {
        return new ReferenceEntry(
            key,
            new TextRange(
                new Position(line, character),
                new Position(endLine < 0 ? line : endLine, endCharacter < 0 ? character + key.Name.Length : endCharacter)),
            kind,
            fromMacro);
    }

    /// <summary>Interleaved keys, every kind, a macro flag, a multi-line range.</summary>
    private static List<ReferenceEntry> Mixed()
    {
        return
        [
            At(Function("a"), 1, 4),
            At(Function("b"), 2, 8, kind: ReferenceKind.AddressOf),
            At(Function("a"), 3, 0, fromMacro: true),
            At(new SymbolKey(null, "health", SymbolKind.Field), 4, 9, kind: ReferenceKind.FieldWrite),
            At(Function("b"), 5, 2, endLine: 7, endCharacter: 1),
            At(Function("a"), 9, 12, kind: ReferenceKind.MethodCall),
            At(new SymbolKey(null, "MAX", SymbolKind.Macro), 10, 3, kind: ReferenceKind.Definition),
        ];
    }

    [Fact]
    public void IteratingYieldsTheEntriesInSourceOrder_Exactly()
    {
        List<ReferenceEntry> entries = Mixed();
        ReferenceTable table = ReferenceTable.Create([.. entries]);

        Assert.Equal(entries, table.ToList());
        Assert.Equal(entries.Count, table.Length);
        for ( int index = 0; index < entries.Count; index++ )
        {
            Assert.Equal(entries[index], table[index]);
        }
    }

    [Fact]
    public void EntriesForAKey_AreThatKeysEntries_InSourceOrder()
    {
        List<ReferenceEntry> entries = Mixed();
        ReferenceTable table = ReferenceTable.Create([.. entries]);

        foreach ( SymbolKey key in entries.Select(entry => entry.Key).Distinct() )
        {
            List<ReferenceEntry> found = [];
            foreach ( ReferenceEntry entry in table.EntriesFor(key) )
            {
                found.Add(entry);
            }

            Assert.Equal(entries.Where(entry => entry.Key == key), found);
            Assert.True(table.Contains(key));
        }

        Assert.False(table.Contains(Function("absent")));
        Assert.Equal(0, table.EntriesFor(Function("absent")).Count);
        Assert.Equal(4, table.Keys.Length);
    }

    [Fact]
    public void ARangeTooWideToPack_KeepsEveryEntryAsGiven()
    {
        // A column past 65,535 and a range spanning more lines than that: neither fits the packed
        // form, so the table must hold the entries whole rather than truncate them.
        List<ReferenceEntry> entries =
        [
            At(Function("a"), 1, 70_000),
            At(Function("b"), 2, 0, endLine: 80_000, endCharacter: 5),
            At(Function("a"), 3, 1),
        ];

        ReferenceTable table = ReferenceTable.Create([.. entries]);

        Assert.Equal(entries, table.ToList());
        Assert.Equal(entries.Where(entry => entry.Key == Function("a")), Collect(table.EntriesFor(Function("a"))));
    }

    [Fact]
    public void TwoKeysSharingAHashCode_AnswerOnlyForThemselves()
    {
        // Found, not assumed: the first two distinct keys this process hashes alike.
        Dictionary<int, SymbolKey> seen = [];
        SymbolKey first = default;
        SymbolKey second = default;
        for ( int index = 0; index < 2_000_000; index++ )
        {
            SymbolKey key = Function("k" + index);
            if ( seen.TryGetValue(key.GetHashCode(), out SymbolKey earlier) )
            {
                first = earlier;
                second = key;
                break;
            }

            seen[key.GetHashCode()] = key;
        }

        Assert.NotEqual(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        ReferenceTable table = ReferenceTable.Create([At(first, 1, 0), At(second, 2, 0), At(first, 3, 0)]);

        Assert.Equal([1, 3], Collect(table.EntriesFor(first)).Select(entry => entry.Range.Start.Line));
        Assert.Equal([2], Collect(table.EntriesFor(second)).Select(entry => entry.Range.Start.Line));
    }

    [Fact]
    public void AnEmptyTable_AndACollectionExpression_BehaveAsAnArrayWould()
    {
        Assert.True(ReferenceTable.Empty.IsEmpty);
        Assert.Empty(ReferenceTable.Empty);
        Assert.False(ReferenceTable.Empty.Contains(Function("a")));

        ReferenceTable built = [At(Function("a"), 1, 0)];
        Assert.Single(built);
    }

    private static List<ReferenceEntry> Collect(ReferenceTable.KeyEntries entries)
    {
        List<ReferenceEntry> found = [];
        foreach ( ReferenceEntry entry in entries )
        {
            found.Add(entry);
        }

        return found;
    }
}
