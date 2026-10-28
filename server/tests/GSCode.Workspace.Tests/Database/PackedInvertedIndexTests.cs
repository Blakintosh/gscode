using System.Collections.Immutable;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// <see cref="PackedInvertedIndex{TKey}.FilesFor"/> hands out one array per change to a key rather
/// than a fresh copy per call, since a bare-name lookup per call site copying thousands of paths was
/// most of a large workspace's lint sweep. These pin the half of that which can go wrong: a write
/// must never be hidden behind an array handed out before it, and an array already handed out must
/// never change under its holder.
/// </summary>
public class PackedInvertedIndexTests
{
    private const string Key = "__init__";

    private static PackedInvertedIndex<string> WithFiles(params string[] paths)
    {
        PackedInvertedIndex<string> index = new(StringComparer.Ordinal);
        foreach ( string path in paths )
        {
            index.Apply(path, [], [Key]);
        }

        return index;
    }

    [Fact]
    public void RepeatedReads_WithNoWriteBetween_ShareOneArray()
    {
        PackedInvertedIndex<string> index = WithFiles(@"c:\a.gsc", @"c:\b.gsc");

        ImmutableArray<string> first = index.FilesFor(Key);
        ImmutableArray<string> second = index.FilesFor(Key);

        Assert.True(first == second);
        Assert.Equal([@"c:\a.gsc", @"c:\b.gsc"], first.Order());
    }

    [Fact]
    public void AFileAdded_AfterARead_IsInTheNextRead()
    {
        PackedInvertedIndex<string> index = WithFiles(@"c:\a.gsc", @"c:\b.gsc");
        ImmutableArray<string> before = index.FilesFor(Key);

        index.Apply(@"c:\c.gsc", [], [Key]);

        Assert.Equal([@"c:\a.gsc", @"c:\b.gsc", @"c:\c.gsc"], index.FilesFor(Key).Order());
        Assert.Equal([@"c:\a.gsc", @"c:\b.gsc"], before.Order());
    }

    [Fact]
    public void AFileRemoved_AfterARead_IsGoneFromTheNextRead()
    {
        PackedInvertedIndex<string> index = WithFiles(@"c:\a.gsc", @"c:\b.gsc", @"c:\c.gsc");
        ImmutableArray<string> before = index.FilesFor(Key);

        index.Apply(@"c:\b.gsc", [Key], []);

        Assert.Equal([@"c:\a.gsc", @"c:\c.gsc"], index.FilesFor(Key).Order());
        Assert.Equal(3, before.Length);
    }

    [Fact]
    public void AnEditThatKeepsTheKey_KeepsTheArray()
    {
        PackedInvertedIndex<string> index = WithFiles(@"c:\a.gsc", @"c:\b.gsc");
        ImmutableArray<string> before = index.FilesFor(Key);

        // Re-applying a file that still carries the key changes nothing about the key's files.
        index.Apply(@"c:\a.gsc", [Key], [Key, "other"]);

        Assert.True(before == index.FilesFor(Key));
    }

    [Fact]
    public void TheLastFileRemoved_LeavesTheKeyEmpty()
    {
        PackedInvertedIndex<string> index = WithFiles(@"c:\a.gsc", @"c:\b.gsc");
        index.FilesFor(Key);

        index.Apply(@"c:\a.gsc", [Key], []);
        index.Apply(@"c:\b.gsc", [Key], []);

        Assert.Empty(index.FilesFor(Key));
    }
}
