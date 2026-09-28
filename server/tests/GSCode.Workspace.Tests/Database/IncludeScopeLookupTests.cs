using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// `FunctionInIncludeScope` answers for ONE name what `FunctionsInIncludeScope` answers for every
/// name, and must not differ from it anywhere. Signature help asks it per keystroke inside an
/// argument list on a merge dialect, where building the whole scope to read one entry was the cost.
///
/// The full list is held here as the reference implementation, the way `BoundedLookupTests` holds
/// the old two-pass shadowing, and the two are compared across the shapes the shadowing rule
/// exists to distinguish: an overlay that re-declares a name, an overlay that DROPS one, a sibling
/// mod nobody can see, and the same name declared by two files in scope at once.
/// </summary>
public class IncludeScopeLookupTests
{
    private static readonly TextRange s_someRange = TextRange.FromCoordinates(0, 0, 0, 1);

    private static ScriptRecord Record(string path, string contextId, string relativePath, params string[] functions)
    {
        ImmutableArray<FunctionSymbol>.Builder symbols = ImmutableArray.CreateBuilder<FunctionSymbol>();
        foreach ( string name in functions )
        {
            symbols.Add(new FunctionSymbol
            {
                Name = name,
                KeyName = name,
                Namespace = "",
                NameRange = s_someRange,
                FullRange = s_someRange,
            });
        }

        return new ScriptRecord
        {
            Path = path,
            Language = ScriptLanguage.Gsc,
            ContextId = contextId,
            ContentHash = 0,
            RelativePath = relativePath,
            Functions = symbols.ToImmutable(),
        };
    }

    private const string AskingPath = @"c:\raw\maps\caller.gsc";

    /// <summary>
    /// The asking file, two files it includes, an overlay re-declaring a name at one of them, an
    /// overlay that drops a name at another, and a sibling mod's copy the asker cannot see.
    /// </summary>
    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(AskingPath, "raw", @"maps\caller.gsc", "own", "shared"));
        store.Upsert(Record(@"c:\raw\maps\a.gsc", "raw", @"maps\a.gsc", "helper", "shared"));
        store.Upsert(Record(@"c:\raw\maps\b.gsc", "raw", @"maps\b.gsc", "dropped", "helper"));
        store.Upsert(Record(@"c:\mods\m\maps\@a.gsc", "mod:m", @"maps\a.gsc", "helper"));
        store.Upsert(Record(@"c:\mods\m\maps\@b.gsc", "mod:m", @"maps\b.gsc", "other"));
        store.Upsert(Record(@"c:\mods\n\maps\@a.gsc", "mod:n", @"maps\a.gsc", "helper"));
        return store;
    }

    private static readonly ImmutableArray<string> s_included =
        [RelativePathIndex.Normalize(@"maps\a.gsc"), RelativePathIndex.Normalize(@"maps\b.gsc")];

    /// <summary>What the full list would have answered for one name.</summary>
    private static FunctionSymbol? FromTheFullList(LanguageStore store, string askingContextId, string keyName)
    {
        foreach ( FunctionSymbol function in DatabaseQueries.FunctionsInIncludeScope(
            store, askingContextId, AskingPath, s_included) )
        {
            if ( string.Equals(function.KeyName, keyName, StringComparison.Ordinal) )
            {
                return function;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("raw", "own")]
    [InlineData("raw", "shared")]
    [InlineData("raw", "helper")]
    [InlineData("raw", "dropped")]
    [InlineData("raw", "other")]
    [InlineData("raw", "absent")]
    [InlineData("mod:m", "own")]
    [InlineData("mod:m", "shared")]
    [InlineData("mod:m", "helper")]
    [InlineData("mod:m", "dropped")]
    [InlineData("mod:m", "other")]
    [InlineData("mod:n", "helper")]
    [InlineData("mod:n", "dropped")]
    public void TheBoundedLookupAgreesWithTheFullList(string askingContextId, string keyName)
    {
        LanguageStore store = Workspace();

        FunctionSymbol? expected = FromTheFullList(store, askingContextId, keyName);
        FunctionSymbol? actual = DatabaseQueries.FunctionInIncludeScope(
            store, askingContextId, AskingPath, s_included, keyName);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AnOverlayThatDropsAName_HidesTheRawCopyFromTheScopeToo()
    {
        // mod m's copy of maps\b declares only `other`, so the engine never loads raw b at all and
        // `dropped` is not in scope for anyone asking as mod m.
        LanguageStore store = Workspace();

        Assert.NotNull(DatabaseQueries.FunctionInIncludeScope(store, "raw", AskingPath, s_included, "dropped"));
        Assert.Null(DatabaseQueries.FunctionInIncludeScope(store, "mod:m", AskingPath, s_included, "dropped"));
    }

    [Fact]
    public void ASiblingModsCopy_IsNeitherOfferedNorAllowedToShadow()
    {
        // mod n's overlay of maps\a is invisible to mod m and to raw, so raw's `helper` stands for
        // them — the case ApplyShadowing's own doc says HasOverlayAt exists to get right.
        LanguageStore store = Workspace();

        FunctionSymbol? asRaw = DatabaseQueries.FunctionInIncludeScope(store, "raw", AskingPath, s_included, "helper");

        Assert.NotNull(asRaw);
        Assert.Equal(FromTheFullList(store, "raw", "helper"), asRaw);
    }
}
