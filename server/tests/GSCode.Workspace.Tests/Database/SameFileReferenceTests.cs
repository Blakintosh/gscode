using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Asking the reference query for ONE FILE must return exactly what asking it wide and keeping that
/// file would have. Document highlight asks it that way on every cursor move, where it used to build
/// every location in the workspace to keep the handful in the open document.
///
/// The corpus test proves the equality over every stock declaration, and stock corpora have no mod
/// overlays — so the case that needs pinning by hand is the one the shadow rule exists for. It is
/// also the case narrowing could plausibly have broken: `ApplyShadowing` decides over the SET, and a
/// set narrowed to one file no longer contains the overlay that shadows it.
/// </summary>
public class SameFileReferenceTests
{
    private static readonly SymbolKey s_main = new(null, "main", SymbolKind.Function);

    private static TextRange At(int line)
    {
        return TextRange.FromCoordinates(line, 0, line, 4);
    }

    private static ScriptRecord Record(string path, string contextId, string relativePath, params int[] callLines)
    {
        ImmutableArray<ReferenceEntry>.Builder references = ImmutableArray.CreateBuilder<ReferenceEntry>();
        foreach ( int line in callLines )
        {
            references.Add(new ReferenceEntry(s_main, At(line), ReferenceKind.Call));
        }

        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = relativePath,
            References = references.ToImmutable(),
            PathCallTargets = ImmutableArray<PathCallReference>.Empty,
        };
    }

    /// <summary>A raw file and a mod overlay at the same relative path, both calling the key.</summary>
    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\maps\a.gsc", "raw", @"maps\a.gsc", 1, 2));
        store.Upsert(Record(@"c:\mods\m\maps\@a.gsc", "mod:m", @"maps\a.gsc", 3));
        store.Upsert(Record(@"c:\raw\maps\b.gsc", "raw", @"maps\b.gsc", 4));
        return store;
    }

    private static List<string> Describe(ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found)
    {
        List<string> lines = [.. found.Select(hit => hit.Record.Path + "|" + hit.Entry.Range)];
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> WideThenFiltered(LanguageStore store, string askingContextId, string path)
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> wide =
            DatabaseQueries.FindAllReferences(new ScriptDatabase(), [store], askingContextId, s_main);

        return Describe([.. wide.Where(hit => hit.Record.Path == path)]);
    }

    private static List<string> AskedForOneFile(LanguageStore store, string askingContextId, string path)
    {
        return Describe(DatabaseQueries.FindAllReferences(
            new ScriptDatabase(), [store], askingContextId, s_main, onlyPath: path));
    }

    [Theory]
    [InlineData("raw", @"c:\raw\maps\a.gsc")]
    [InlineData("raw", @"c:\raw\maps\b.gsc")]
    [InlineData("raw", @"c:\mods\m\maps\@a.gsc")]
    [InlineData("mod:m", @"c:\raw\maps\a.gsc")]
    [InlineData("mod:m", @"c:\raw\maps\b.gsc")]
    [InlineData("mod:m", @"c:\mods\m\maps\@a.gsc")]
    [InlineData("mod:n", @"c:\raw\maps\a.gsc")]
    public void AskingForOneFileEqualsAskingWideAndKeepingIt(string askingContextId, string path)
    {
        LanguageStore store = Workspace();

        Assert.Equal(
            WideThenFiltered(store, askingContextId, path),
            AskedForOneFile(store, askingContextId, path));
    }

    [Fact]
    public void ARawFileAnOverlayReplaces_HasNoReferencesOfItsOwn()
    {
        // The case the narrowing could have broken. Asked as mod m, raw maps\a is shadowed and
        // contributes nothing — and it must still contribute nothing when it is the only file asked
        // about, though the overlay that shadows it is then not in the set.
        LanguageStore store = Workspace();

        Assert.Empty(AskedForOneFile(store, "mod:m", @"c:\raw\maps\a.gsc"));
        Assert.NotEmpty(AskedForOneFile(store, "mod:m", @"c:\mods\m\maps\@a.gsc"));

        // A sibling mod does not shadow raw, so raw keeps its own.
        Assert.NotEmpty(AskedForOneFile(store, "mod:n", @"c:\raw\maps\a.gsc"));
    }

    [Fact]
    public void AFileNothingReferences_ComesBackEmptyRatherThanWide()
    {
        LanguageStore store = Workspace();

        Assert.Empty(AskedForOneFile(store, "raw", @"c:\raw\maps\absent.gsc"));
    }
}
