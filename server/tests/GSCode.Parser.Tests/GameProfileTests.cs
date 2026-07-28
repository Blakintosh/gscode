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
        // MW2 has no bundled data (CoD4 does now, from its wordfile).
        GameProfile mw2 = GameProfile.ByName("mw2")!;

        Assert.Null(mw2.DataFilePrefix);
        Assert.Null(mw2.ApiFileName(ScriptLanguage.Gsc));
        Assert.Empty(mw2.BundledDataFileNames);
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
        Assert.Equal("TA_TOOLS_PATH", profile.RootEnvironmentVariable);
        Assert.Equal(@"share\raw", profile.RawSubfolder);
        Assert.Equal("mods", profile.ModsSubfolder);
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
    public void OnlyBlackOps3IsVerified()
    {
        // Supported means "filled in and in scope"; Verified means "verified for the game".
        // Only BO3 is the latter.
        Assert.Same(GameProfile.BlackOps3, GameProfile.All.Single(static profile => profile.Verified));
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
    public void EveryShortNameIsUnique()
    {
        Assert.Equal(GameProfile.All.Length, GameProfile.All.Select(static profile => profile.ShortName).Distinct().Count());
    }
}
