using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Completion offers only what the active dialect has. The keyword lists are shared, but
/// <see cref="GscKeywords.IsAvailable"/> filters them per profile (mirroring the lexer's keyword
/// gating), and the global objects come from <see cref="GameProfile.GlobalObjectNames"/>. So CoD4
/// is not offered BO3-only constructs, and BO3 keeps everything.
/// </summary>
public class DialectCompletionTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    [Theory]
    [InlineData("foreach")] // MW2+
    [InlineData("do")]      // BO3
    [InlineData("class")]   // BO3
    [InlineData("new")]     // BO3
    [InlineData("function")] // BO3
    [InlineData("const")]   // BO3
    [InlineData("autoexec")] // BO3
    [InlineData("private")]  // BO3
    [InlineData("#using")]  // BO3 import
    [InlineData("#namespace")]
    [InlineData("#insert")]
    [InlineData("#precache")]
    public void Cod4DoesNotOfferBlackOps3Constructs(string keyword)
    {
        Assert.False(GscKeywords.IsAvailable(keyword, Cod4));
        Assert.True(GscKeywords.IsAvailable(keyword, Bo3));
    }

    [Fact]
    public void Cod4OffersIncludeButBlackOps3DoesNot()
    {
        // #include is the Infinity Ward import; #using is BO3's.
        Assert.True(GscKeywords.IsAvailable("#include", Cod4));
        Assert.False(GscKeywords.IsAvailable("#include", Bo3));
    }

    [Theory]
    [InlineData("if")]
    [InlineData("for")]
    [InlineData("while")]
    [InlineData("return")]
    [InlineData("waittill")]
    [InlineData("thread")]
    [InlineData("#define")]
    [InlineData("#if")]
    public void UniversalKeywordsAreOfferedEverywhere(string keyword)
    {
        Assert.True(GscKeywords.IsAvailable(keyword, Cod4));
        Assert.True(GscKeywords.IsAvailable(keyword, Bo3));
    }

    [Fact]
    public void GlobalObjectsComeFromTheProfile()
    {
        // self/level/game/anim are universal.
        Assert.Contains("self", Cod4.GlobalObjectNames);
        Assert.Contains("level", Cod4.GlobalObjectNames);
        Assert.Contains("anim", Cod4.GlobalObjectNames);

        // world (BO3+) and classes (BO3 class system) are not in the Infinity Ward line.
        Assert.Contains("world", Bo3.GlobalObjectNames);
        Assert.DoesNotContain("world", Cod4.GlobalObjectNames);
        Assert.Contains("classes", Bo3.GlobalObjectNames);
        Assert.DoesNotContain("classes", Cod4.GlobalObjectNames);
    }

    // --- Path completion for INLINE path calls ---
    //
    // The Infinity Ward line reaches another file by naming its path in the middle of an
    // expression — `maps\mp\_utility::foo()` — with no import at all. So the folder-walk the
    // directives get has to work in ordinary code too, or there is no way to discover what a path
    // continues into. The profile is passed explicitly rather than through GameProfile.Active, so
    // these cannot be perturbed by a test that mutates it.

    private const string Raw = @"C:\cod4\raw";

    private static readonly FakeFileSystem PathWorld = new FakeFileSystem()
        .AddFile(@$"{Raw}\maps\mp\_utility.gsc", "helper()\n{\n}\n")
        .AddFile(@$"{Raw}\maps\mp\_load.gsc", "load()\n{\n}\n")
        .AddFile(@$"{Raw}\maps\mp\gametypes\dm.gsc", "main()\n{\n}\n")
        .AddFile(@$"{Raw}\common_scripts\utility.gsc", "u()\n{\n}\n");

    private static CompletionEngine BuildEngine()
    {
        RootConfig config = RootConfig.Create(true, Raw, @"C:\cod4\mods", [], PathWorld);
        PathResolver resolver = new(config, PathWorld);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, PathWorld, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        return new CompletionEngine(database, BuiltinApiSet.Load(api), ObjectFields.Load(api));
    }

    /// <summary>Completes at the end of `line`, placed inside a function body, for one dialect.</summary>
    private static ImmutableArray<CompletionEntry> CompleteInCode(string line, GameProfile profile)
    {
        CompletionEngine engine = BuildEngine();

        string text = "main()\n{\n    " + line + "\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\mp\test.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(text),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable(),
            profile);

        return engine.Complete(result, "raw", new Position(2, 4 + line.Length), profile: profile);
    }

    [Fact]
    public void AnInlinePathInCode_OffersTheNextSegment()
    {
        ImmutableArray<CompletionEntry> entries = CompleteInCode(@"maps\", Cod4);

        Assert.Contains(entries, e => e.Label == "mp" && e.Kind == CompletionKind.PathSegment);
    }

    [Fact]
    public void AnInlinePath_DescendsToFilesLikeTheDirectivesDo()
    {
        ImmutableArray<CompletionEntry> entries = CompleteInCode(@"maps\mp\", Cod4);

        Assert.Contains(entries, e => e.Label == "_utility" && e.Kind == CompletionKind.PathFile);
        Assert.Contains(entries, e => e.Label == "gametypes" && e.Kind == CompletionKind.PathSegment);

        // Nothing from another root leaks in — the typed prefix is what selects candidates.
        Assert.DoesNotContain(entries, e => e.Label == "common_scripts");
    }

    [Fact]
    public void AFolderStillInsertsItsSeparatorAndReopens()
    {
        CompletionEntry folder = Assert.Single(CompleteInCode(@"maps\", Cod4), e => e.Label == "mp");

        Assert.Equal(@"mp\", folder.InsertText);
        Assert.True(folder.RetriggerCompletion);
    }

    [Fact]
    public void BlackOps3DoesNotOfferPathsInCode()
    {
        // BO3 has no inline path calls, so a '\' in an expression means nothing there and the
        // ordinary statement list must be what comes back.
        ImmutableArray<CompletionEntry> entries = CompleteInCode(@"maps\", Bo3);

        Assert.DoesNotContain(entries, e => e.Kind is CompletionKind.PathSegment or CompletionKind.PathFile);
    }

    [Fact]
    public void ABareIdentifierIsNotTreatedAsAPath()
    {
        // The separator is the whole disambiguation: without requiring one, every identifier in a
        // function body would look like a path's first segment and the list would become paths.
        ImmutableArray<CompletionEntry> entries = CompleteInCode("map", Cod4);

        Assert.DoesNotContain(entries, e => e.Kind is CompletionKind.PathSegment or CompletionKind.PathFile);
    }

    [Fact]
    public void IncludeDirectivesGetPathCompletion()
    {
        // #include is the merge dialects' import and was simply missing from the directive scan,
        // so the whole Infinity Ward line got no path completion on the one directive it writes.
        CompletionEngine engine = BuildEngine();

        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\mp\test.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("#include maps\\\nmain()\n{\n}\n"),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable(),
            Cod4);

        ImmutableArray<CompletionEntry> entries = engine.Complete(
            result, "raw", new Position(0, 14), profile: Cod4);

        Assert.Contains(entries, e => e.Label == "mp" && e.Kind == CompletionKind.PathSegment);
    }
}
