using System.Collections.Immutable;
using System.Linq;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The reported bug: a mod overlay and the raw copy it shadows can both declare the same
/// function under the same namespace at the same script-relative path
/// (<c>scripts\zm\_zm.gsc</c> in both <c>share\raw</c> and <c>mods\zm_grief</c>). The engine only
/// ever loads the overlay copy, but go-to-definition/find-references from inside the mod file
/// showed BOTH declarations, because the shared reference query never applied the overlay
/// shadowing rule <see cref="DatabaseQueries.LookupFunctions"/> already applies elsewhere.
/// </summary>
public class OverlayShadowingReferenceTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private const string Mods = @"C:\bo3\mods";

    private static ScriptDatabase BuildWorkspace()
    {
        const string source =
            "#namespace zm;\nfunction player_too_many_weapons_monitor_takeaway_sequence()\n{\n}\n";

        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\zm\_zm.gsc", source)
            .AddFile(@$"{Mods}\zm_grief\scripts\zm\_zm.gsc", source);

        RootConfig config = RootConfig.Create(true, Raw, Mods, [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        return database;
    }

    [Fact]
    public void AModOverlayShadowsTheRawFileItReplaces_SoOnlyOneDefinitionIsFound()
    {
        ScriptDatabase database = BuildWorkspace();
        SymbolKey key = new(
            "zm", "player_too_many_weapons_monitor_takeaway_sequence", SymbolKind.Function);

        // Asking from inside the mod overlay itself, exactly as in the report.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> found =
            DatabaseQueries.FindAllReferences(database, [database.Gsc], "mod:zm_grief", key);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> definitions =
            [.. found.Where(static r => r.Entry.Kind == ReferenceKind.Definition)];

        // Only the overlay's own declaration should survive — the raw copy it shadows is dead
        // code the engine never loads, and must not appear as a second "definition".
        ScriptRecord kept = Assert.Single(definitions).Record;
        Assert.Equal("mod:zm_grief", kept.ContextId);
    }

    private static readonly GSCode.Core.Text.TextRange SomeRange =
        new(new GSCode.Core.Text.Position(1, 1), new GSCode.Core.Text.Position(1, 5));

    private static ScriptRecord FunctionRecord(
        string path, string contextId, string relativePath, string keyName, int paramCount)
    {
        ImmutableArray<ParameterSymbol> parameters =
            [.. Enumerable.Range(0, paramCount).Select(static i => new ParameterSymbol("p" + i, false, ""))];

        return new ScriptRecord
        {
            Path = path,
            ContextId = contextId,
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = relativePath,
            Functions =
            [
                new FunctionSymbol
                {
                    Name = keyName,
                    KeyName = keyName,
                    Namespace = "",
                    NameRange = SomeRange,
                    FullRange = SomeRange,
                    Parameters = parameters,
                },
            ],
        };
    }

    /// <summary>
    /// The same overlay-shadowing hole, in <see cref="DatabaseQueries.FunctionsInIncludeScope"/>
    /// (the unqualified-completion query for #include dialects): raw and overlay both declare
    /// `util`, both pass the include-scope check, and a plain <c>Dictionary.TryAdd</c> kept
    /// whichever came first in store-enumeration order — arbitrarily offering the SHADOWED raw
    /// signature instead of the overlay's.
    /// </summary>
    [Fact]
    public void FunctionsInIncludeScope_PrefersTheOverlayOverTheRawCopyItShadows()
    {
        // These two specific paths are load-bearing: LanguageStore enumerates its ConcurrentDictionary
        // in HASH-BUCKET order, not insertion order, so the raw record enumerates FIRST for this pair
        // (confirmed empirically) — the exact case a bare Dictionary.TryAdd gets wrong.
        LanguageStore store = new();
        store.Upsert(FunctionRecord(@"C:\raw0\maps\_utility.gsc", "raw", @"maps\_utility", "util", paramCount: 1));
        store.Upsert(FunctionRecord(
            @"C:\mods0\zm_grief\maps\_utility.gsc", "mod:zm_grief", @"maps\_utility", "util", paramCount: 2));

        ImmutableArray<FunctionSymbol> functions = DatabaseQueries.FunctionsInIncludeScope(
            store, "mod:zm_grief", askingPath: "", includedPaths: [@"maps\_utility"]);

        FunctionSymbol kept = Assert.Single(functions);
        Assert.Equal(2, kept.Parameters.Length);
    }

    /// <summary>
    /// The gap the per-name check left open: the raw copy declared `foo`, but the overlay REPLACING
    /// it no longer does — the engine still never loads the raw file, but nothing in `matches` ever
    /// contained the overlay's record to compare against, since a lookup for "foo" specifically
    /// never finds a file that does not declare it.
    /// </summary>
    [Fact]
    public void LookupFunctions_HidesTheWholeRawFile_EvenWhenTheOverlayNoLongerDeclaresTheName()
    {
        LanguageStore store = new();
        store.Upsert(FunctionRecord(@"C:\raw0\maps\_utility.gsc", "raw", @"maps\_utility", "foo", paramCount: 1));
        // The overlay replaces the whole file; its own copy dropped `foo` and declares `bar` instead.
        store.Upsert(FunctionRecord(
            @"C:\mods0\zm_grief\maps\_utility.gsc", "mod:zm_grief", @"maps\_utility", "bar", paramCount: 0));

        ImmutableArray<ResolvedFunction> found = DatabaseQueries.LookupFunctions(
            store, "mod:zm_grief", askingPath: "", namespaceName: "", keyName: "foo", includePrivate: true);

        Assert.Empty(found);
    }

    private static ScriptRecord ClassRecord(
        string path, string contextId, string relativePath, string keyName, string? parentKeyName)
    {
        return new ScriptRecord
        {
            Path = path,
            ContextId = contextId,
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = relativePath,
            Classes =
            [
                new ClassSymbol
                {
                    Name = keyName,
                    KeyName = keyName,
                    Namespace = "",
                    ParentKeyName = parentKeyName,
                    NameRange = SomeRange,
                    FullRange = SomeRange,
                },
            ],
        };
    }

    /// <summary>
    /// Same hole, in <see cref="DatabaseQueries.AllVisibleClasses"/> (class-name completion): its
    /// doc comment claims parity with <see cref="DatabaseQueries.LookupClasses"/>, but only
    /// <c>LookupClasses</c> actually calls the shadowing rule — this one dedups with a bare
    /// <c>TryAdd</c>, same as <see cref="DatabaseQueries.FunctionsInIncludeScope"/> above.
    /// </summary>
    [Fact]
    public void AllVisibleClasses_PrefersTheOverlayOverTheRawCopyItShadows()
    {
        LanguageStore store = new();
        store.Upsert(ClassRecord(@"C:\raw\maps\_zombiemode.gsc", "raw", @"maps\_zombiemode", "zombiemode", null));
        store.Upsert(ClassRecord(
            @"C:\mods\zm_grief\maps\_zombiemode.gsc", "mod:zm_grief", @"maps\_zombiemode", "zombiemode", "base"));

        ImmutableArray<ClassSymbol> classes = DatabaseQueries.AllVisibleClasses(
            store, "mod:zm_grief", askingPath: "", importedPaths: [@"maps\_zombiemode"]);

        ClassSymbol kept = Assert.Single(classes);
        Assert.Equal("base", kept.ParentKeyName);
    }
}
