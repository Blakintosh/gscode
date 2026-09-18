using System.Collections.Immutable;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// The reported bug: <see cref="InsertCache"/> keyed its entries with
/// <see cref="StringComparer.OrdinalIgnoreCase"/>, but <see cref="GSCode.Core.Paths.PathUtil.NormalizeAbsolute"/>
/// keeps EXACT case on Linux — a case-sensitive filesystem, where <c>Base.gsh</c> and <c>base.gsh</c>
/// really can be two different files. An ignore-case comparer here collided them into one cache
/// entry regardless of which platform actually produced the resolved paths, so whichever file was
/// read first silently supplied its content for both.
///
/// Testable independent of the real OS: this cache takes whatever resolved-path string it is
/// given and is never itself responsible for the platform-specific lowercasing PathUtil applies
/// before a path reaches it, so two literal strings differing only by case exercise the cache's
/// own comparer directly.
/// </summary>
public class InsertCacheTests
{
    private sealed class StubFileSystem : IFileSystem
    {
        public bool FileExists(string absolutePath)
        {
            throw new NotSupportedException();
        }

        public bool DirectoryExists(string absolutePath)
        {
            throw new NotSupportedException();
        }

        public string ReadAllText(string absolutePath)
        {
            throw new NotSupportedException();
        }

        public DateTime GetLastWriteTimeUtc(string absolutePath)
        {
            return DateTime.UnixEpoch;
        }

        public IEnumerable<string> EnumerateFilesWithExtensions(string directory, ImmutableArray<string> extensions)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public void GetOrAdd_TreatsPathsDifferingOnlyByCaseAsDistinctEntries()
    {
        InsertCache cache = new();
        StubFileSystem files = new();

        InsertedFile upper = new(@"C:\raw\Base.gsh", SourceText.From("A"), ImmutableArray<Token>.Empty);
        InsertedFile lower = new(@"C:\raw\base.gsh", SourceText.From("B"), ImmutableArray<Token>.Empty);

        InsertedFile? first = cache.GetOrAdd(@"C:\raw\Base.gsh", files, () => upper);
        InsertedFile? second = cache.GetOrAdd(@"C:\raw\base.gsh", files, () => lower);

        Assert.Same(upper, first);
        Assert.Same(lower, second);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Store_TreatsPathsDifferingOnlyByCaseAsDistinctEntries()
    {
        InsertCache cache = new();

        HeaderContribution upper = new(ImmutableArray<MacroDefinition>.Empty, ImmutableArray<InsertEdge>.Empty);
        HeaderContribution lower = new(ImmutableArray<MacroDefinition>.Empty, ImmutableArray<InsertEdge>.Empty);

        cache.Store(@"C:\raw\Base.gsh", upper);
        cache.Store(@"C:\raw\base.gsh", lower);

        Assert.True(cache.TryGet(@"C:\raw\Base.gsh", out HeaderContribution foundUpper));
        Assert.True(cache.TryGet(@"C:\raw\base.gsh", out HeaderContribution foundLower));
        Assert.Same(upper, foundUpper);
        Assert.Same(lower, foundLower);
    }
}
