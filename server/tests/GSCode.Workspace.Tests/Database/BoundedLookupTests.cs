using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// LookupFunctions now decides overlay shadowing per record, inside the walk, so the walk can stop
/// at a limit. These pin that against the rule it replaced — every match collected first, then
/// ApplyShadowing over the lot — across the overlay shapes that rule exists for, and that a capped
/// answer is exactly the front of the full one.
/// </summary>
public class BoundedLookupTests
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

    /// <summary>
    /// Raw files, an overlay that shadows one of them and re-declares the name, an overlay that
    /// shadows another and DROPS it, a sibling mod, and a workspace file — every case the shadowing
    /// rule distinguishes, in one store.
    /// </summary>
    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\maps\a.gsc", "raw", @"maps\a.gsc", "main", "helper"));
        store.Upsert(Record(@"c:\raw\maps\b.gsc", "raw", @"maps\b.gsc", "main"));
        store.Upsert(Record(@"c:\raw\maps\c.gsc", "raw", @"maps\c.gsc", "main"));
        store.Upsert(Record(@"c:\mods\m\maps\a.gsc", "mod:m", @"maps\a.gsc", "main"));
        store.Upsert(Record(@"c:\mods\m\maps\b.gsc", "mod:m", @"maps\b.gsc", "other"));
        store.Upsert(Record(@"c:\mods\n\maps\c.gsc", "mod:n", @"maps\c.gsc", "main"));
        store.Upsert(Record(@"c:\ws\d.gsc", @"workspace:c:\ws", @"d.gsc", "main"));
        return store;
    }

    /// <summary>The rule as it was: collect every visible match, then shadow the whole list.</summary>
    private static ImmutableArray<ResolvedFunction> TwoPass(LanguageStore store, string askingContextId, string keyName)
    {
        ImmutableArray<ResolvedFunction>.Builder matches = ImmutableArray.CreateBuilder<ResolvedFunction>();
        foreach ( string path in store.FilesDeclaring(keyName) )
        {
            if ( !store.TryGet(path, out ScriptRecord record) || !ScriptDatabase.CanSee(askingContextId, record.ContextId) )
            {
                continue;
            }

            foreach ( FunctionSymbol function in record.Functions )
            {
                if ( function.KeyName == keyName )
                {
                    matches.Add(new ResolvedFunction(function, record));
                }
            }
        }

        return DatabaseQueries.ApplyShadowing(
            matches.ToImmutable(), static match => match.Record, static match => match.Function.KeyName, store, askingContextId);
    }

    private static IEnumerable<string> PathsOf(ImmutableArray<ResolvedFunction> found)
    {
        return found.Select(static match => match.Record.Path);
    }

    [Theory]
    [InlineData("raw", "main")]
    [InlineData("mod:m", "main")]
    [InlineData("mod:n", "main")]
    [InlineData(@"workspace:c:\ws", "main")]
    [InlineData("mod:m", "helper")]
    [InlineData("raw", "helper")]
    [InlineData("mod:m", "other")]
    public void TheWalkShadowsExactlyAsTheSecondPassDid(string askingContextId, string keyName)
    {
        LanguageStore store = Workspace();

        ImmutableArray<ResolvedFunction> expected = TwoPass(store, askingContextId, keyName);
        ImmutableArray<ResolvedFunction> actual = DatabaseQueries.LookupFunctions(store, askingContextId, "", null, keyName);

        Assert.Equal(PathsOf(expected), PathsOf(actual));
    }

    [Fact]
    public void AnOverlayThatDropsAName_StillShadowsTheRawCopyOfIt()
    {
        // mod m's copy of maps\b declares only `other`, so the engine never loads raw b's `main`.
        LanguageStore store = Workspace();

        ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(store, "mod:m", "", null, "main");

        Assert.DoesNotContain(@"c:\raw\maps\b.gsc", PathsOf(found));
        Assert.Contains(@"c:\raw\maps\c.gsc", PathsOf(found));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ACappedAnswer_IsTheFrontOfTheFullOne(int limit)
    {
        LanguageStore store = Workspace();

        ImmutableArray<ResolvedFunction> full = DatabaseQueries.LookupFunctions(store, "raw", "", null, "main");
        ImmutableArray<ResolvedFunction> capped = DatabaseQueries.LookupFunctions(store, "raw", "", null, "main", limit: limit);

        Assert.Equal(PathsOf(full).Take(limit), PathsOf(capped));
    }
}
