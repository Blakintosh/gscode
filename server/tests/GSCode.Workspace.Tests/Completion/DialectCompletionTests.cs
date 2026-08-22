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

    [Theory]
    [InlineData("#define")]
    [InlineData("#if")]
    [InlineData("#elif")]
    [InlineData("#else")]
    [InlineData("#endif")]
    public void Cod4HasNoPreprocessorDirectives(string directive)
    {
        // Reported from a screenshot: typing '#' at the top of a CoD4 file offered #define and the
        // whole #if chain. IsAvailable gated five directives by capability and then let anything
        // else beginning with '#' through, on a comment claiming the rest "exist across the whole
        // lineage". They do not — `#define` appears in one file per IW-line game, always the same
        // commented-out block of C in _hud.gsc, and the #if family in none.
        Assert.False(GscKeywords.IsAvailable(directive, Cod4));
        Assert.True(GscKeywords.IsAvailable(directive, Bo3));
    }

    [Fact]
    public void TheAnimtreePairIsOfferedInEveryDialect()
    {
        // The genuinely universal directives, and all that is left of the old blanket rule: 193
        // CoD4 files use #using_animtree and 54 use #animtree, against 66 and 45 in BO3.
        Assert.True(GscKeywords.IsAvailable("#using_animtree", Cod4));
        Assert.True(GscKeywords.IsAvailable("#using_animtree", Bo3));
        Assert.True(GscKeywords.IsAvailable("#animtree", Cod4));
        Assert.True(GscKeywords.IsAvailable("#animtree", Bo3));
    }

    [Fact]
    public void AnimtreeIsOfferedInABodyAndNeverAtFileScope()
    {
        // #animtree is a directive by spelling and an expression atom by grammar — the argument to
        // UseAnimTree( #animtree ), which is a call. Across the five corpora it appears in 415 files
        // and not once at the start of a line, so file scope is a position it cannot occupy. It sat
        // in TopLevelKeywords, which is exactly where the screenshot showed it.
        Assert.DoesNotContain("#animtree", GscKeywords.TopLevelKeywords);
        Assert.Contains("#animtree", GscKeywords.BodyDirectives);
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
    // #define and #if used to be here, on the same assumption IsAvailable's fallthrough made. They
    // are BO3's alone; Cod4HasNoPreprocessorDirectives above is the corrected claim. What is left of
    // the directive family that really is universal is the animtree pair.
    [InlineData("#using_animtree")]
    [InlineData("#animtree")]
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
    [InlineData("precache")]
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
    public void ThePrecacheSnippetIsBlackOps3sAloneAndHandsOffItsAssetType()
    {
        // This one was contributed by the extension until it was the last file breaking the rule
        // the rest of this section exists for: #precache is BO3's directive, and four games were
        // offered it. The client carried the asset types as a snippet CHOICE LIST, which is why it
        // needed two files — one per world. The body here names none of them: the tab stop lands
        // inside the quotes and Retrigger reopens the list, so PrecacheAssetTypes stays the only
        // place the vocabulary is written down and the world split is answered once.
        CompletionEntry snippet = Assert.Single(TopLevelCompletions(Bo3), e => e.Label == "precache");

        Assert.Equal(CompletionKind.Snippet, snippet.Kind);
        Assert.StartsWith("#precache(", snippet.InsertText, StringComparison.Ordinal);
        Assert.True(snippet.RetriggerCompletion);

        // No asset type is written into the body — that is the whole point of the handoff.
        Assert.DoesNotContain("model", snippet.InsertText, StringComparison.Ordinal);
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

    [Fact]
    public void TypingAHashAtTopLevelInCod4_OffersOnlyWhatCod4Has()
    {
        // The reported case, end to end rather than through IsAvailable alone: this is the list in
        // the screenshot. CoD4 has two top-level directives and was being offered eight.
        ImmutableArray<CompletionEntry> entries = DirectivesAfterHash(Cod4);

        Assert.Equal(
            new[] { "#include", "#using_animtree" },
            entries.Select(e => e.Label).OrderBy(l => l, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void TypingAHashAtTopLevelInBlackOps3_IsUnchanged()
    {
        // The gate must not cost BO3 anything: it is the game that has all of them.
        ImmutableArray<CompletionEntry> entries = DirectivesAfterHash(Bo3);

        foreach ( string directive in (string[])["#using", "#insert", "#namespace", "#precache", "#define", "#if"] )
        {
            Assert.Contains(entries, e => e.Label == directive);
        }

        // Still not #animtree, which is a body position in every game including this one.
        Assert.DoesNotContain(entries, e => e.Label == "#animtree");
    }

    /// <summary>What a '#' typed at file scope offers, for one dialect.</summary>
    private static ImmutableArray<CompletionEntry> DirectivesAfterHash(GameProfile profile)
    {
        CompletionEngine engine = BuildEngine();
        ParseResult result = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\mp\test.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("#\n"),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable(),
            profile);

        return engine.Complete(result, "raw", new Position(0, 1), profile: profile);
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

        // #animtree is the whole of it, and the list being one item long is the point rather than a
        // weakness of the test. This asserted #if and #define until they were found to be BO3's
        // alone; what keeps the branch from returning an empty list on this dialect — the reason it
        // exists — is the one directive CoD4 can write in a body.
        Assert.Contains(entries, e => e.Label == "#animtree");

        // #insert is a header directive and CoD4 has no headers; #include is its import and is top
        // level only; #if and #define need a preprocessor CoD4 does not have. None belongs here.
        Assert.DoesNotContain(entries, e => e.Label is "#insert" or "#include" or "#if" or "#define");
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
