using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// <see cref="DatabaseQueries.FindReferencesReaching"/> must return what scoping the full reference
/// list returned. The corpus test proves it over every stock declaration, but stock corpora have no
/// mod overlays, so the shadowing half is pinned here: an overlay that references the key without
/// reaching the declaring file still shadows the raw copy it replaces.
/// </summary>
public class ReferencesReachingTests
{
    private static readonly SymbolKey s_main = new(null, "main", SymbolKind.Function);

    private static TextRange At(int line)
    {
        return TextRange.FromCoordinates(line, 0, line, 4);
    }

    private static ScriptRecord Record(
        string path,
        string contextId,
        string relativePath,
        bool declaresMain = false,
        int[]? callsMainOn = null,
        string[]? includes = null,
        (string Path, int Line)[]? pathCalls = null)
    {
        ImmutableArray<ReferenceEntry>.Builder references = ImmutableArray.CreateBuilder<ReferenceEntry>();
        ImmutableArray<FunctionSymbol>.Builder functions = ImmutableArray.CreateBuilder<FunctionSymbol>();

        if ( declaresMain )
        {
            functions.Add(TestRecords.Function("main") with { NameRange = At(0), FullRange = At(0) });
            references.Add(new ReferenceEntry(s_main, At(0), ReferenceKind.Definition));
        }

        foreach ( int line in callsMainOn ?? [] )
        {
            references.Add(new ReferenceEntry(s_main, At(line), ReferenceKind.Call));
        }

        ImmutableArray<PathCallReference>.Builder targets = ImmutableArray.CreateBuilder<PathCallReference>();
        foreach ( (string Path, int Line) pathCall in pathCalls ?? [] )
        {
            references.Add(new ReferenceEntry(s_main, At(pathCall.Line), ReferenceKind.Call));
            targets.Add(new PathCallReference(pathCall.Path, At(pathCall.Line)));
        }

        ImmutableArray<DependencyEdge>.Builder edges = ImmutableArray.CreateBuilder<DependencyEdge>();
        foreach ( string include in includes ?? [] )
        {
            edges.Add(new DependencyEdge(include, "", IsInsert: false, At(0)));
        }

        return TestRecords.At(path, contextId, relativePath) with
        {
            Functions = functions.ToImmutable(),
            References = references.ToImmutable(),
            Dependencies = edges.ToImmutable(),
            PathCallTargets = targets.ToImmutable(),
        };
    }

    private static LanguageStore Workspace()
    {
        LanguageStore store = new();
        store.Upsert(Record(@"c:\raw\maps\a.gsc", "raw", @"maps\a.gsc", declaresMain: true, callsMainOn: [5]));
        store.Upsert(Record(@"c:\raw\maps\user.gsc", "raw", @"maps\user.gsc", callsMainOn: [3], includes: [@"maps\a"]));
        store.Upsert(Record(@"c:\raw\maps\caller.gsc", "raw", @"maps\caller.gsc", pathCalls: [(@"maps\a", 7)]));
        store.Upsert(Record(@"c:\raw\maps\other.gsc", "raw", @"maps\other.gsc", declaresMain: true, callsMainOn: [2]));

        // mod m replaces maps\user with a copy that still calls main but no longer includes maps\a.
        store.Upsert(Record(@"c:\mods\m\maps\user.gsc", "mod:m", @"maps\user.gsc", callsMainOn: [9]));
        return store;
    }

    private static List<string> Describe(ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found)
    {
        List<string> lines = [];
        foreach ( (ScriptRecord Record, ReferenceEntry Entry) item in found )
        {
            lines.Add($"{item.Record.Path}|{item.Entry.Range}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Theory]
    [InlineData("raw")]
    [InlineData("mod:m")]
    public void TheScopedQueryAgreesWithScopingTheFullList(string askingContextId)
    {
        LanguageStore store = Workspace();
        ImmutableArray<LanguageStore> stores = [store];
        ScriptDatabase database = new();

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> old = DatabaseQueries.ScopeToIncludeGraph(
            DatabaseQueries.FindAllReferences(database, stores, askingContextId, s_main), @"maps\a.gsc", GameProfile.Cod4);
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped =
            DatabaseQueries.FindReferencesReaching(stores, askingContextId, s_main, @"maps\a.gsc", GameProfile.Cod4);

        Assert.Equal(Describe(old), Describe(scoped));
    }

    [Fact]
    public void FromTheMod_TheShadowedRawCopyIsGone_AndTheUnrelatedMainNeverCounted()
    {
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found = DatabaseQueries.FindReferencesReaching(
            [Workspace()], "mod:m", s_main, @"maps\a.gsc", GameProfile.Cod4);

        List<string> paths = [.. found.Select(static item => item.Record.Path).Distinct()];
        Assert.Contains(@"c:\raw\maps\a.gsc", paths);
        Assert.Contains(@"c:\raw\maps\caller.gsc", paths);
        Assert.DoesNotContain(@"c:\raw\maps\user.gsc", paths);
        Assert.DoesNotContain(@"c:\raw\maps\other.gsc", paths);
    }

    [Fact]
    public void AnEditThatDropsTheInclude_TakesTheFileOutOfTheDependents()
    {
        LanguageStore store = Workspace();
        Assert.Contains(@"c:\raw\maps\user.gsc", store.FilesNaming(RelativePathIndex.Normalize(@"maps\a")));

        store.Upsert(Record(@"c:\raw\maps\user.gsc", "raw", @"maps\user.gsc", callsMainOn: [3]));

        Assert.DoesNotContain(@"c:\raw\maps\user.gsc", store.FilesNaming(RelativePathIndex.Normalize(@"maps\a")));
        Assert.Contains(@"c:\raw\maps\caller.gsc", store.FilesNaming(RelativePathIndex.Normalize(@"maps\a")));
    }
}
