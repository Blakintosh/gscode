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

    [Fact]
    public void GlobalObjectsAreOfferedInAFunctionBody()
    {
        // Goes through the ENGINE, not the profile. The test above passed for a long time while
        // the list was empty: the globals were concatenated onto the keyword list and then run
        // through GscKeywords.IsAvailable, which ends at the profile's keyword set — and no global
        // object is a keyword in any dialect, so every one of them was dropped in every game.
        ImmutableArray<CompletionEntry> entries = CompleteInCode("", Cod4);

        Assert.Contains(entries, e => e.Label == "self" && e.Kind == CompletionKind.Variable);
        Assert.Contains(entries, e => e.Label == "level" && e.Kind == CompletionKind.Variable);
        Assert.Contains(entries, e => e.Label == "game" && e.Kind == CompletionKind.Variable);
        Assert.Contains(entries, e => e.Label == "anim" && e.Kind == CompletionKind.Variable);
    }

    [Fact]
    public void TheGlobalObjectsOfferedAreTheDialectsOwn()
    {
        // Matched on the "global" detail rather than the label alone, so an unrelated field or
        // function that happens to be named `world` cannot make this pass or fail by accident.
        Assert.Contains(CompleteInCode("", Bo3), e => e.Label == "world" && e.Detail == "global");
        Assert.DoesNotContain(CompleteInCode("", Cod4), e => e.Label == "world" && e.Detail == "global");

        Assert.Contains(CompleteInCode("", Bo3), e => e.Label == "classes" && e.Detail == "global");
        Assert.DoesNotContain(CompleteInCode("", Cod4), e => e.Label == "classes" && e.Detail == "global");
    }

    [Fact]
    public void GlobalObjectsAreNotOfferedAtTopLevel()
    {
        // Outside a function body only declarations and directives are legal, and `self` there is
        // not a thing anyone can write.
        CompletionEngine engine = BuildEngine();
        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\mp\test.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("\nmain()\n{\n}\n"),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable(),
            Cod4);

        ImmutableArray<CompletionEntry> entries = engine.Complete(result, "raw", new Position(0, 0), profile: Cod4);

        Assert.DoesNotContain(entries, e => e.Detail == "global");
    }

    // --- Dialect-gated snippets ---
    //
    // These used to be contributed by the extension, which registers a snippet per LANGUAGE ID and
    // so could not ask which of the five games was active. CoD4 was offered a foreach loop it
    // cannot run, and taking it produced a call the server then reported as unresolved.

    [Fact]
    public void Cod4IsNotOfferedTheForeachSnippet()
    {
        ImmutableArray<CompletionEntry> entries = CompleteInCode("", Cod4);

        Assert.DoesNotContain(entries, e => e.Label == "foreach" && e.Kind == CompletionKind.Snippet);
        Assert.DoesNotContain(entries, e => e.Label == "foreachkv");

        // Nor the bare keyword, which is the same claim by the other route.
        Assert.DoesNotContain(entries, e => e.Label == "foreach");
    }

    [Fact]
    public void BlackOps3IsOfferedTheForeachSnippet()
    {
        ImmutableArray<CompletionEntry> entries = CompleteInCode("", Bo3);

        CompletionEntry snippet = Assert.Single(entries, e => e.Label == "foreach");

        Assert.Equal(CompletionKind.Snippet, snippet.Kind);
        Assert.Contains("${1:value}", snippet.InsertText, StringComparison.Ordinal);

        // Exactly one item labelled `foreach`: the snippet REPLACES the bare keyword rather than
        // sitting beside it.
        Assert.Contains(entries, e => e.Label == "foreachkv");
    }

    [Theory]
    [InlineData("class")]
    [InlineData("new")]
    [InlineData("funcauto")]
    [InlineData("funcpriv")]
    [InlineData("using")]
    [InlineData("insert")]
    [InlineData("namespace")]
    public void Cod4IsNotOfferedBlackOps3Snippets(string label)
    {
        Assert.DoesNotContain(CompleteInCode("", Cod4), e => e.Label == label);
        Assert.DoesNotContain(TopLevelCompletions(Cod4), e => e.Label == label);
        Assert.True(
            CompleteInCode("", Bo3).Any(e => e.Label == label)
                || TopLevelCompletions(Bo3).Any(e => e.Label == label),
            label + " should still be offered somewhere in BO3");
    }

    [Fact]
    public void TheImportSnippetFollowsTheDialect()
    {
        // CoD4 merges with #include and BO3 imports with #using; neither has the other's.
        Assert.Contains(TopLevelCompletions(Cod4), e => e.Label == "include");
        Assert.DoesNotContain(TopLevelCompletions(Cod4), e => e.Label == "using");

        Assert.Contains(TopLevelCompletions(Bo3), e => e.Label == "using");
        Assert.DoesNotContain(TopLevelCompletions(Bo3), e => e.Label == "include");
    }

    [Fact]
    public void TheScriptDocSnippetFollowsTheDialect()
    {
        // Every dialect has SOME ScriptDoc form, so this one is chosen rather than filtered: both
        // games offer `doc`, and the body is the one their own scripts use.
        CompletionEntry cod4 = Assert.Single(TopLevelCompletions(Cod4), e => e.Label == "doc");
        CompletionEntry bo3 = Assert.Single(TopLevelCompletions(Bo3), e => e.Label == "doc");

        Assert.StartsWith("/*", cod4.InsertText, StringComparison.Ordinal);
        Assert.StartsWith("/@", bo3.InsertText, StringComparison.Ordinal);
    }

    /// <summary>Completes at the top of a file, outside any function body, for one dialect.</summary>
    private static ImmutableArray<CompletionEntry> TopLevelCompletions(GameProfile profile)
    {
        CompletionEngine engine = BuildEngine();
        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\mp\test.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("\n"),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable(),
            profile);

        return engine.Complete(result, "raw", new Position(0, 0), profile: profile);
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

    /// <summary>
    /// Completes at the end of `line`, placed inside a function body, for one dialect.
    ///
    /// The declaration opens the way the dialect does: BO3 needs the `function` keyword, and a bare
    /// `main()` there is not a declaration at all — so the cursor landed at TOP LEVEL and every BO3
    /// case here was asserting against the top-level list without saying so. The keyword only
    /// lengthens line 0, so the completion position below is unaffected.
    /// </summary>
    private static ImmutableArray<CompletionEntry> CompleteInCode(string line, GameProfile profile)
    {
        CompletionEngine engine = BuildEngine();

        string opening = profile.HasFunctionKeyword ? "function " : "";
        string text = opening + "main()\n{\n    " + line + "\n}\n";
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

    [Theory]
    [InlineData("hp = maxhealth/")]
    [InlineData("hp = maxhealth/2")]
    [InlineData("frac = count/total")]
    public void UnspacedDivisionIsNotAnInlinePath(string line)
    {
        // '/' is the division operator (and the start of a comment); only '\' begins a path. It is
        // also a completion trigger character, so accepting it as a separator turned every unspaced
        // division on the four inline-path dialects into a path query that matches nothing — and an
        // empty list with IsIncomplete false is cached and filtered client-side, so the rest of the
        // identifier got no suggestions either.
        ImmutableArray<CompletionEntry> entries = CompleteInCode(line, Cod4);

        Assert.DoesNotContain(entries, e => e.Kind is CompletionKind.PathSegment or CompletionKind.PathFile);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void ABackslashPathStillCompletesAfterTheSeparatorNarrowing()
    {
        // The other half of the same claim: narrowing the scan to '\' must not cost the feature.
        Assert.Contains(
            CompleteInCode(@"maps\mp\_ut", Cod4),
            e => e.Label == "_utility" && e.Kind == CompletionKind.PathFile);
    }

    [Fact]
    public void AHashInAFunctionBodyOffersDirectivesOnADialectWithNoHashStrings()
    {
        // CoD4, WaW and MW2 have no #"..." literal, so the in-body '#' branch had nothing to offer
        // and returned a hard empty list. '#' is a trigger character, so that list popped — the
        // "feels dead" symptom the trigger characters were added to fix.
        ImmutableArray<CompletionEntry> entries = CompleteInCode("#", Cod4);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Label == "#if");
        Assert.Contains(entries, e => e.Label == "#define");

        // #insert is a header directive and CoD4 has no headers; #include is its import and is top
        // level only. Neither belongs in the body list.
        Assert.DoesNotContain(entries, e => e.Label is "#insert" or "#include");
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
