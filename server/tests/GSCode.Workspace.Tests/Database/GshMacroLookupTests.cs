using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// A .gsh serves both languages, so its records live in the shared GSH store rather than
/// either LanguageStore. Go-to-definition on a macro inserted from a header therefore has to
/// look outside the asking file's language world — the reported bug was that it never did,
/// so IS_TRUE from shared.gsh resolved to nothing from array_shared.gsc.
/// </summary>
public class GshMacroLookupTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static ScriptDatabase BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\shared\shared.gsh",
                "#define IS_TRUE(__a) (isdefined(__a) && __a)\n#define REGISTER_SYSTEM(__n) register(__n)\n")
            .AddFile(
                @$"{Raw}\scripts\shared\array_shared.gsc",
                "#insert scripts\\shared\\shared.gsh;\n#namespace array;\nfunction run( v )\n{\n    if ( IS_TRUE( v ) )\n    {\n    }\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        return database;
    }

    private static SymbolKey MacroKey(string name)
    {
        // Macro names are case-sensitive and carry no namespace.
        return new SymbolKey(null, name, SymbolKind.Macro);
    }

    [Fact]
    public void GshStore_HoldsTheMacroDefinition_NotTheLanguageStore()
    {
        ScriptDatabase database = BuildWorkspace();

        // The language store holds the USE site in array_shared.gsc, which is why the symbol
        // resolves under the cursor — but it holds no declaration, which is why go-to-definition
        // came back empty. That asymmetry is the bug.
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> inLanguage =
            DatabaseQueries.FindReferences(database.Gsc, "raw", MacroKey("IS_TRUE"));

        Assert.Contains(inLanguage, found => found.Entry.Kind == ReferenceKind.MacroUse);
        Assert.DoesNotContain(inLanguage, found => found.Entry.Kind == ReferenceKind.Definition);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> inGsh =
            DatabaseQueries.FindGshReferences(database, "raw", MacroKey("IS_TRUE"));

        Assert.Contains(inGsh, found => found.Entry.Kind == ReferenceKind.Definition);
        Assert.EndsWith("shared.gsh", inGsh[0].Record.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryMacroInTheHeader_IsReachable()
    {
        ScriptDatabase database = BuildWorkspace();

        Assert.NotEmpty(DatabaseQueries.FindGshReferences(database, "raw", MacroKey("REGISTER_SYSTEM")));
    }

    [Fact]
    public void MacroNamesAreCaseSensitive()
    {
        // The PDF is explicit that macro names are case-sensitive, unlike everything else.
        ScriptDatabase database = BuildWorkspace();

        Assert.Empty(DatabaseQueries.FindGshReferences(database, "raw", MacroKey("is_true")));
    }

    [Fact]
    public void UnknownMacro_FindsNothing()
    {
        ScriptDatabase database = BuildWorkspace();

        Assert.Empty(DatabaseQueries.FindGshReferences(database, "raw", MacroKey("NOT_A_MACRO")));
    }
}
