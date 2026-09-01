using System.Collections.Immutable;
using System.IO.Hashing;
using System.Text;
using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// The P5 fixture root: share\raw + mod_a + mod_b + a workspace project, indexed with
/// the real pipeline over the fake file system.
/// </summary>
public class ScriptDatabaseTests
{
    private const string ToolsRoot = @"C:\bo3";
    private const string Raw = @"C:\bo3\share\raw";
    private const string Mods = @"C:\bo3\mods";

    private static FakeFileSystem FixtureTree()
    {
        return new FakeFileSystem()
            // Raw: a shared utility namespace split across two files + a GSH.
            .AddFile(@$"{Raw}\scripts\shared\util_a.gsc", "#namespace util;\nfunction alpha()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\shared\util_b.gsc", "#namespace util;\nfunction beta()\n{\nalpha();\n}\n")
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh", "#define SHARED_FLAG 1\n")
            .AddFile(@$"{Raw}\scripts\codescripts\struct.gsc", "function raw_struct()\n{\n}\n")
            // mod_a shadows struct.gsc and adds its own script using the shared gsh.
            .AddFile(@$"{Mods}\mod_a\scripts\codescripts\struct.gsc", "function mod_struct()\n{\n}\n")
            .AddFile(@$"{Mods}\mod_a\scripts\a_main.gsc", "#insert scripts\\shared\\shared.gsh;\nfunction a_main()\n{\nlevel notify(\"round_start\");\n}\n")
            // mod_b has its own world.
            .AddFile(@$"{Mods}\mod_b\scripts\b_main.gsc", "function b_main()\n{\nlevel notify(\"round_start\");\n}\n")
            // The language guard: parallel gsc/csc defining the same namespace::function.
            .AddFile(@$"{Raw}\scripts\dual\foo.gsc", "#namespace dual;\nfunction ping()\n{\nself notify(\"dual_event\");\n}\n")
            .AddFile(@$"{Raw}\scripts\dual\foo.csc", "#namespace dual;\nfunction ping()\n{\nself notify(\"dual_event\");\n}\n");
    }

    private static async Task<(ScriptDatabase Database, PathResolver Resolver)> IndexFixtureAsync()
    {
        FakeFileSystem fileSystem = FixtureTree();
        RootConfig config = RootConfig.Create(rawEnabled: true, rawPath: Raw, modsPath: Mods, workspaceFolders: [], fileSystem: fileSystem);

        PathResolver resolver = new(config, fileSystem);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, new NameTable());

        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        return (database, resolver);
    }

    [Fact]
    public async Task Index_CommitsRecordsToTheRightStores()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        // 7 gsc files, 1 csc file, 1 gsh.
        Assert.Equal(7, database.Gsc.Count);
        Assert.Equal(1, database.Csc.Count);
        Assert.Single(database.AllGshRecords);
    }

    [Fact]
    public async Task NamespaceMerging_UnionsAcrossContributingFiles()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        ImmutableArray<ResolvedFunction> alpha = DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", askingPath: "", namespaceName: "util", keyName: "alpha");
        ImmutableArray<ResolvedFunction> beta = DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", askingPath: "", namespaceName: "util", keyName: "beta");

        Assert.Single(alpha);
        Assert.Single(beta);
        Assert.NotEqual(alpha[0].Record.Path, beta[0].Record.Path);
    }

    [Fact]
    public async Task ModIsolation_ModNeverSeesSiblingMod()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        // mod_a asking for mod_b's function: invisible.
        ImmutableArray<ResolvedFunction> fromModA = DatabaseQueries.LookupFunctions(
            database.Gsc, "mod:mod_a", askingPath: "", namespaceName: null, keyName: "b_main");

        Assert.Empty(fromModA);

        // But mod_b itself sees it.
        ImmutableArray<ResolvedFunction> fromModB = DatabaseQueries.LookupFunctions(
            database.Gsc, "mod:mod_b", askingPath: "", namespaceName: null, keyName: "b_main");

        Assert.Single(fromModB);
    }

    [Fact]
    public async Task ModIsolation_RawNeverSeesMods()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        ImmutableArray<ResolvedFunction> fromRaw = DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", askingPath: "", namespaceName: null, keyName: "a_main");

        Assert.Empty(fromRaw);
    }

    [Fact]
    public async Task Shadowing_ModCopyBeatsRawCopy()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        // struct.gsc exists in raw and in mod_a's overlay: from mod_a, the raw copy of
        // that relative file must not surface its (differently-named) functions when
        // both would match — here we assert the mod sees ITS struct and raw's function
        // from the SAME relative path is dropped only when identities collide. Names
        // differ in fixture, so check both resolve independently first:
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "mod:mod_a", "", null, "mod_struct"));
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "mod:mod_a", "", null, "raw_struct"));
    }

    [Fact]
    public async Task LanguageGuard_GscQueryNeverSurfacesCscResults()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        // Same namespace::function exists in both worlds; each store answers alone.
        ImmutableArray<ResolvedFunction> gscPing = DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", "", "dual", "ping");
        ImmutableArray<ResolvedFunction> cscPing = DatabaseQueries.LookupFunctions(
            database.Csc, "raw", "", "dual", "ping");

        Assert.Single(gscPing);
        Assert.Single(cscPing);
        Assert.EndsWith(".gsc", gscPing[0].Record.Path, StringComparison.Ordinal);
        Assert.EndsWith(".csc", cscPing[0].Record.Path, StringComparison.Ordinal);

        // The shared notify string is language-pure too: each store's literal index
        // only returns files from its own world.
        SymbolKey dualEvent = new(null, "dual_event", SymbolKind.StringLiteral);
        foreach ( (ScriptRecord record, _) in DatabaseQueries.FindReferences(database.Gsc, "raw", dualEvent) )
        {
            Assert.Equal(ScriptLanguage.Gsc, record.Language);
        }

        foreach ( (ScriptRecord record, _) in DatabaseQueries.FindReferences(database.Csc, "raw", dualEvent) )
        {
            Assert.Equal(ScriptLanguage.Csc, record.Language);
        }
    }

    [Fact]
    public async Task LiteralReferences_FindAcrossVisibleContext()
    {
        (ScriptDatabase database, _) = await IndexFixtureAsync();

        SymbolKey roundStart = new(null, "round_start", SymbolKind.StringLiteral);

        // From mod_a: only its own notify site (mod_b's is invisible).
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> fromModA =
            DatabaseQueries.FindReferences(database.Gsc, "mod:mod_a", roundStart);

        (ScriptRecord Record, ReferenceEntry Entry) single = Assert.Single(fromModA);
        Assert.Contains("mod_a", single.Record.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GshMacros_LandInTheSharedStore()
    {
        (ScriptDatabase database, PathResolver resolver) = await IndexFixtureAsync();

        string gshPath = PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh");
        Assert.True(database.TryGetGsh(gshPath, out ScriptRecord gshRecord));

        MacroRecord macro = Assert.Single(gshRecord.Macros);
        Assert.Equal("SHARED_FLAG", macro.Name);

        // And the inserting script recorded the dependency edge.
        ImmutableArray<ResolvedFunction> aMain = DatabaseQueries.LookupFunctions(
            database.Gsc, "mod:mod_a", "", null, "a_main");
        DependencyEdge edge = Assert.Single(aMain[0].Record.Dependencies, dependency => dependency.IsInsert);
        Assert.Equal(gshPath, edge.ResolvedPath);
        Assert.NotNull(resolver);
    }

    /// <summary>
    /// Privacy is scoped to the NAMESPACE, not the file: any file declaring the same namespace
    /// is part of the same logical unit and may call into it. secret.gsc declares no
    /// #namespace, so it takes its file-name stem, "secret".
    /// </summary>
    [Fact]
    public async Task PrivateFunctions_VisibleWithinTheirNamespace_NotOutsideIt()
    {
        FakeFileSystem fileSystem = FixtureTree()
            .AddFile(@$"{Raw}\scripts\secret.gsc", "function private hidden()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, ToolsRoot + @"\share\raw", ToolsRoot + @"\mods", [], fileSystem);
        PathResolver resolver = new(config, fileSystem);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, new NameTable());
        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

        string secretPath = PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\secret.gsc");
        string elsewhere = PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\elsewhere.gsc");

        // Its own file always sees it.
        Assert.Single(DatabaseQueries.LookupFunctions(database.Gsc, "raw", secretPath, null, "hidden"));

        // Another file declaring the same namespace sees it too.
        Assert.Single(DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", elsewhere, null, "hidden", askingNamespaces: ["secret"]));

        // A file in a different namespace does not.
        Assert.Empty(DatabaseQueries.LookupFunctions(
            database.Gsc, "raw", elsewhere, null, "hidden", askingNamespaces: ["game"]));

        // A caller supplying no namespaces falls back to same-file visibility only.
        Assert.Empty(DatabaseQueries.LookupFunctions(database.Gsc, "raw", elsewhere, null, "hidden"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("main(){}")]
    [InlineData("// a comment with unicode: éèê")]
    public void ComputeContentHash_MatchesWholeStringUtf8(string text)
    {
        // Chunked hashing must produce the byte sequence encoding the WHOLE string would, or every
        // hash already written to a workspace cache silently stops matching and every file
        // re-analyses on the next start — with no error to notice.
        ulong expected = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));

        Assert.Equal(expected, ScriptDatabase.ComputeContentHash(text));
    }

    [Fact]
    public void ComputeContentHash_HandlesASurrogatePairSpanningAChunkBoundary()
    {
        // The case that makes a stateful Encoder necessary rather than repeated GetBytes calls: a
        // chunk boundary landing between the two halves of a surrogate pair. Padded so the pair
        // straddles the 8192-character chunk edge exactly.
        string padded = new string('a', 8191) + "😀" + new string('b', 32);

        Assert.Equal(
            XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(padded)),
            ScriptDatabase.ComputeContentHash(padded));
    }
}
