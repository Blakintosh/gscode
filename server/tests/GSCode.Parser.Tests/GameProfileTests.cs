using GSCode.Core;
using GSCode.Core.Symbols;
using Xunit;

namespace GSCode.Parser.Tests;

public class GameProfileTests
{
    [Fact]
    public void BlackOps3_Defaults_ExposeExpectedExtensionsAndData()
    {
        GameProfile profile = GameProfile.BlackOps3;

        Assert.Equal("t7", profile.Id);
        Assert.Equal(".gsc", profile.ServerScriptExtension);
        Assert.Equal(".csc", profile.ClientScriptExtension);
        Assert.Equal(".gsh", profile.HeaderExtension);

        // Data files are named from the prefix, not hardcoded at the loaders.
        Assert.Equal("t7", profile.DataFilePrefix);
        Assert.Equal("t7_api_gsc.json", profile.ApiFileName(ScriptLanguage.Gsc));
        Assert.Equal("t7_api_csc.json", profile.ApiFileName(ScriptLanguage.Csc));
        Assert.Contains("t7_object_fields.json", profile.BundledDataFileNames);
        Assert.Contains("t7_stock_scripts.txt", profile.BundledDataFileNames);
    }

    [Fact]
    public void AGameThatShipsNoData_HasNoDataFileNames()
    {
        // A CORE, now that all five supported games ship a library — MW2 was the example until its
        // own was built from CoD4's shared wordfile list plus its corpus sweep.
        GameProfile mw3 = GameProfile.ByName("mw3")!;

        Assert.Null(mw3.DataFilePrefix);
        Assert.Null(mw3.ApiFileName(ScriptLanguage.Gsc));
        Assert.Empty(mw3.BundledDataFileNames);
    }

    [Fact]
    public void ModernWarfare2_DeclaresItsDataFiles()
    {
        GameProfile mw2 = GameProfile.ByName("mw2")!;

        Assert.Equal("mw2", mw2.DataFilePrefix);
        Assert.Equal("mw2_api_gsc.json", mw2.ApiFileName(ScriptLanguage.Gsc));
        // No client scripts in the Infinity Ward line, so no client library is promised.
        Assert.DoesNotContain("mw2_api_csc.json", mw2.BundledDataFileNames);
        Assert.Contains("mw2_radiant_keys.json", mw2.BundledDataFileNames);
    }

    [Fact]
    public void BlackOps3_HasEveryTreyarchFeature()
    {
        GameProfile profile = GameProfile.BlackOps3;

        Assert.True(profile.HasClientScripts);
        Assert.True(profile.HasHeaders);
        Assert.True(profile.HasClasses);
        Assert.True(profile.HasFunctionKeyword);
        Assert.True(profile.HasNamespaceDirective);
        Assert.Equal(ImportStyle.Namespace, profile.ImportStyle);
        Assert.Equal(FunctionPointerStyle.Ampersand, profile.FunctionPointerStyle);
        Assert.True(profile.ArraysPassedByReference);
    }

    [Theory]
    [InlineData(ScriptLanguage.Gsc, ".gsc")]
    [InlineData(ScriptLanguage.Csc, ".csc")]
    [InlineData(ScriptLanguage.Gsh, ".gsh")]
    public void ExtensionFor_MapsEachWorld(ScriptLanguage language, string expected)
    {
        Assert.Equal(expected, GameProfile.BlackOps3.ExtensionFor(language));
    }

    [Theory]
    [InlineData(@"c:\ws\scripts\a.gsc", ScriptLanguage.Gsc)]
    [InlineData(@"c:\ws\scripts\a.csc", ScriptLanguage.Csc)]
    [InlineData(@"c:\ws\scripts\a.gsh", ScriptLanguage.Gsh)]
    [InlineData(@"c:\ws\scripts\a.GSC", ScriptLanguage.Gsc)]
    [InlineData(@"c:\ws\scripts\noext", ScriptLanguage.Gsc)]
    public void LanguageFromPath_KeysOffTheExtension(string path, ScriptLanguage expected)
    {
        Assert.Equal(expected, GameProfile.BlackOps3.LanguageFromPath(path));
    }

    [Fact]
    public void ScriptExtensionsAndGlobs_CoverEveryWorldInOrder()
    {
        Assert.Equal(new[] { ".gsc", ".csc", ".gsh" }, GameProfile.BlackOps3.ScriptExtensions.ToArray());
        Assert.Equal(new[] { "*.gsc", "*.csc", "*.gsh" }, GameProfile.BlackOps3.ScriptGlobs.ToArray());
    }

    [Fact]
    public void Active_DefaultsToBlackOps3()
    {
        // Before anything selects a game, the fallback is BO3.
        GameProfile.Select("bo3");
        Assert.Same(GameProfile.BlackOps3, GameProfile.Active);
    }

    [Fact]
    public void Select_ChangesTheActiveProfile()
    {
        try
        {
            GameProfile.Select("cod4");
            Assert.Equal("cod4", GameProfile.Active.ShortName);

            GameProfile.Select("t7");
            Assert.Same(GameProfile.BlackOps3, GameProfile.Active);
        }
        finally
        {
            GameProfile.Select("bo3");
        }
    }

    [Fact]
    public void Select_FallsBackToBlackOps3ForAnUnknownName()
    {
        try
        {
            GameProfile.Select("halo");
            Assert.Same(GameProfile.BlackOps3, GameProfile.Active);
        }
        finally
        {
            GameProfile.Select("bo3");
        }
    }

    [Fact]
    public void TheLineageRunsFromCod4ToBo6InReleaseOrder()
    {
        // Every mainline game from CoD4 to BO6 is listed, release-ordered. CoD4 is first, BO6 last.
        Assert.Equal("cod4", GameProfile.All[0].ShortName);
        Assert.Equal("bo6", GameProfile.All[^1].ShortName);
        Assert.Equal(18, GameProfile.All.Length);

        for ( int i = 1; i < GameProfile.All.Length; i++ )
        {
            Assert.True(
                GameProfile.All[i].ReleaseYear >= GameProfile.All[i - 1].ReleaseYear,
                $"{GameProfile.All[i].ShortName} is out of release order");
        }
    }

    [Fact]
    public void SupportedGamesAreTheFiveVerifiedSpecGames()
    {
        // Only five games have their capabilities verified against real scripts; everything else in
        // the lineage is a CORE (a nameable identity over the base dialect, capabilities unset).
        Assert.Equal(
            new[] { "cod4", "waw", "mw2", "bo1", "bo3" },
            GameProfile.All.Where(static profile => profile.Supported).Select(static profile => profile.ShortName).ToArray());
    }

    [Fact]
    public void BlackOps3IsTheOnlyNamespaceDrivenGame()
    {
        // The whole lineage MERGES except BO3: #include pulls a file's functions into the caller's
        // scope, so the file a function lives in is not part of its name. BO3 alone makes the
        // namespace part of a function's identity.
        //
        // Asserted over every profile, cores included, because five resolution rules read this —
        // how a function is keyed, whether references scope to the include graph, whether a
        // definition narrows to one file — and a core that drifted into the namespace model would
        // silently take BO3's answer for all of them.
        Assert.Equal(
            new[] { "bo3" },
            GameProfile.All.Where(static profile => profile.ResolvesByNamespace).Select(static profile => profile.ShortName).ToArray());
    }

    [Fact]
    public void ResolutionAndDirectiveSpellingAgreeForEveryGameToday()
    {
        // Two separate claims that happen to coincide across the whole lineage: which directive is
        // spelled (#using vs #include), and whether a function's identity carries its namespace.
        // Nothing REQUIRES them to agree, which is why the call sites read whichever one they mean —
        // this test records that the day they diverge, it is a deliberate change to a profile and not
        // a silent drift.
        foreach ( GameProfile profile in GameProfile.All )
        {
            Assert.Equal(profile.ImportStyle == ImportStyle.Namespace, profile.ResolvesByNamespace);
        }
    }

    [Fact]
    public void OnlyBlackOps3HasAPreprocessor()
    {
        // Recorded as a profile invariant rather than left to the completion tests, because the
        // claim is about the LANGUAGE and two layers read it — GscKeywords for what is offered, and
        // the Preprocessor for gscode-2016. Measured over the shipped scripts: #define appears in
        // one file per pre-BO3 game, always the same commented-out block of C in _hud.gsc, and the
        // #if family in none of the four.
        Assert.Equal(
            new[] { "bo3" },
            GameProfile.All.Where(static profile => profile.HasMacros).Select(static profile => profile.ShortName).ToArray());
    }

    [Fact]
    public void MacrosAndHeadersAreSeparateClaimsThatHappenToAgree()
    {
        // A header IS macros, so the two coincide today and the flag could look derivable. It is
        // not: a dialect could define macros in-file with nowhere to put them, and collapsing the
        // two would make that game's support a rewrite rather than a value. Same shape as the
        // ImportStyle/ResolvesByNamespace pair above — recorded so the day they diverge is a
        // deliberate change to a profile and not a silent drift.
        foreach ( GameProfile profile in GameProfile.All )
        {
            Assert.Equal(profile.HasHeaders, profile.HasMacros);
        }
    }

    [Fact]
    public void CoresMatchTheBaseDialect()
    {
        // A core sets no capabilities: it is the base IW-style shape (base keywords, #include merge,
        // path calls) until a contributor fills it in. BO2 is a good example — a Treyarch core with
        // none of the Treyarch specifics (.csc, hash strings) filled in yet.
        GameProfile bo2 = GameProfile.ByName("bo2")!;
        Assert.False(bo2.Supported);
        Assert.False(bo2.Verified);
        Assert.False(bo2.HasClientScripts);
        Assert.False(bo2.HasHashStrings);
        Assert.False(bo2.HasClasses);
        Assert.Equal(GameProfile.BaseKeywords, bo2.Keywords);
        Assert.Equal(ImportStyle.Include, bo2.ImportStyle);
        Assert.True(bo2.HasInlinePathCalls);
    }

    [Fact]
    public void EverySupportedGameIsVerified_AndNoCoreIs()
    {
        // Verified is earned by the per-game corpus gate (GameCorpusTests): the game's own scripts
        // analyse without throwing, parse within budget, and survive the formatter. All five
        // supported games clear it; a core has no game-specific capabilities to prove.
        Assert.Equal(
            new[] { "cod4", "waw", "mw2", "bo1", "bo3" },
            GameProfile.All.Where(static profile => profile.Verified).Select(static profile => profile.ShortName).ToArray());

        Assert.All(
            GameProfile.All.Where(static profile => !profile.Supported),
            static profile => Assert.False(profile.Verified, profile.ShortName));
    }

    [Fact]
    public void TreyarchGamesBeforeBo3_HaveClientScriptsButNotTheRestOfTheBo3Shape()
    {
        // The nice detail from the worksheet: the supported Treyarch games before BO3 (WaW, BO1)
        // shipped .csc, but not headers, classes, the function keyword or #namespace. (BO2 is a core,
        // so its .csc is not filled in yet.)
        foreach ( string name in new[] { "waw", "bo1" } )
        {
            GameProfile game = GameProfile.ByName(name)!;
            Assert.True(game.HasClientScripts, name);
            Assert.False(game.HasHeaders, name);
            Assert.False(game.HasClasses, name);
            Assert.False(game.HasFunctionKeyword, name);
            Assert.Equal(ImportStyle.Include, game.ImportStyle);
            Assert.Equal(FunctionPointerStyle.PathQualified, game.FunctionPointerStyle);
        }
    }

    [Fact]
    public void OnlyMw2HasFileScopeConstants()
    {
        Assert.True(GameProfile.ByName("mw2")!.HasFileScopeConstants);
        Assert.False(GameProfile.BlackOps3.HasFileScopeConstants);
    }

    [Fact]
    public void ForeachIsAFamilyFork_Mw2OnTheIwLineAndBo3OnTheTreyarchLine()
    {
        // foreach is the Infinity Ward line's MW2 (2009) addition; the Treyarch line does NOT get it
        // until BO3, so BO1 has none despite being newer than MW2.
        Assert.False(GameProfile.ByName("cod4")!.HasForeach);
        Assert.False(GameProfile.ByName("waw")!.HasForeach);
        Assert.True(GameProfile.ByName("mw2")!.HasForeach);
        Assert.False(GameProfile.ByName("bo1")!.HasForeach);
        Assert.True(GameProfile.BlackOps3.HasForeach);
    }

    [Fact]
    public void DoWhileIsBlackOps3Only()
    {
        Assert.True(GameProfile.BlackOps3.HasDoWhile);
        Assert.All(
            GameProfile.All.Where(static profile => profile.ShortName != "bo3"),
            static profile => Assert.False(profile.HasDoWhile, profile.ShortName));
    }

    [Fact]
    public void OnlyBlackOps3UsesAtSignScriptDoc()
    {
        // BO3 uses /@ @/; every earlier game fences ScriptDoc with ///ScriptDocBegin.
        Assert.Equal(ScriptDocStyle.AtSign, GameProfile.BlackOps3.ScriptDocStyle);
        Assert.All(
            GameProfile.All.Where(static profile => profile.ShortName != "bo3"),
            static profile => Assert.Equal(ScriptDocStyle.TripleSlash, profile.ScriptDocStyle));
    }

    [Fact]
    public void HashStringsAreATreyarchFeature()
    {
        // #"..." a Treyarch feature; the supported Treyarch games (BO1, BO3) have it and the Infinity
        // Ward games have none. (BO2 is an unfilled core.)
        Assert.True(GameProfile.ByName("bo1")!.HasHashStrings);
        Assert.True(GameProfile.BlackOps3.HasHashStrings);
        Assert.False(GameProfile.ByName("mw2")!.HasHashStrings);
        Assert.False(GameProfile.ByName("cod4")!.HasHashStrings);
    }

    [Fact]
    public void OnlyBlackOps3HasThePrecacheDirective()
    {
        // Every earlier game precaches with function calls, not a #precache directive.
        Assert.True(GameProfile.BlackOps3.HasPrecacheDirective);
        Assert.All(
            GameProfile.All.Where(static profile => profile.ShortName != "bo3"),
            static profile => Assert.False(profile.HasPrecacheDirective));
    }

    [Fact]
    public void EveryTargetedPreBo3GameHasInlinePathCalls_ButBo3DoesNot()
    {
        // maps\mp\_util::foo() -- every pre-BO3 game; BO3 reaches functions only by #using.
        Assert.All(
            GameProfile.All.Where(static profile => profile.Supported && profile.ShortName != "bo3"),
            static profile => Assert.True(profile.HasInlinePathCalls, profile.ShortName));
        Assert.False(GameProfile.BlackOps3.HasInlinePathCalls);
    }

    [Theory]
    [InlineData("bo3")]
    [InlineData("BO3")]
    [InlineData("t7")]
    public void ByName_FindsAGameByShortNameOrId(string name)
    {
        Assert.Same(GameProfile.BlackOps3, GameProfile.ByName(name));
    }

    [Fact]
    public void ByName_ReturnsNullForAnUnknownName()
    {
        Assert.Null(GameProfile.ByName("halo"));
    }

    [Fact]
    public void Select_ReportsWhetherTheNameWasRecognised()
    {
        // The reported symptom was "gscode.game does nothing": package.json offered mw3, bo2,
        // ghosts and aw, none of which has a profile, and selecting one fell back to BO3 without
        // a word — the setting read back exactly as written while the server ran as BO3. The
        // return value is what lets the caller say so.
        try
        {
            Assert.True(GameProfile.Select("cod4"));
            Assert.Equal("cod4", GameProfile.Active.ShortName);

            Assert.False(GameProfile.Select("ghosts"));
            Assert.Equal("bo3", GameProfile.Active.ShortName);

            Assert.False(GameProfile.Select("halo"));
            Assert.Equal("bo3", GameProfile.Active.ShortName);
        }
        finally
        {
            // Active is global, so a test that leaves it moved would change what every later test
            // in this assembly parses.
            GameProfile.Select("bo3");
        }
    }

    [Fact]
    public void Abbreviation_UpperCasesTheShortNameUnlessTheGameSaysOtherwise()
    {
        // The status bar shows this, so it is the game as people write it rather than as the
        // selector spells it: upper-casing renders CoD4 and WaW as COD4 and WAW, and neither is
        // how anyone writes them.
        Assert.Equal("BO3", GameProfile.BlackOps3.Abbreviation);
        Assert.Equal("MW2", GameProfile.ByName("mw2")!.Abbreviation);
        Assert.Equal("BO1", GameProfile.ByName("bo1")!.Abbreviation);
        Assert.Equal("CoD4", GameProfile.ByName("cod4")!.Abbreviation);
        Assert.Equal("WaW", GameProfile.ByName("waw")!.Abbreviation);
    }

    [Fact]
    public void EveryProfileHasANonEmptyAbbreviation()
    {
        // The default is computed from ShortName, so this holds for the unsupported games too and
        // keeps holding as more are added — an empty status bar would be the only symptom.
        foreach ( GameProfile profile in GameProfile.All )
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Abbreviation));
        }
    }

    [Fact]
    public void KeyNamespace_DropsTheNamespaceOnMergeDialects()
    {
        // A merge dialect keys functions by BARE NAME - #include pulls them into the caller's scope -
        // yet it still reports a namespace, defaulting to the file stem. Anything rebuilding a lookup
        // key from a symbol's declared namespace must go through this, or it builds a key nothing is
        // stored under: that is what made every CoD4 CodeLens read "0 references".
        Assert.Null(GameProfile.ByName("cod4")!.KeyNamespace("battlechatter_ai"));
        Assert.Null(GameProfile.ByName("waw")!.KeyNamespace("anything"));

        // BO3 qualifies the call, so the namespace is part of the identity and must survive.
        Assert.Equal("util", GameProfile.BlackOps3.KeyNamespace("util"));

        // An absent namespace is null in either dialect.
        Assert.Null(GameProfile.BlackOps3.KeyNamespace(""));
    }

    [Fact]
    public void EveryShortNameIsUnique()
    {
        Assert.Equal(GameProfile.All.Length, GameProfile.All.Select(static profile => profile.ShortName).Distinct().Count());
    }
}
