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
        GameProfile cod4 = GameProfile.ByName("cod4")!;

        Assert.Null(cod4.DataFilePrefix);
        Assert.Null(cod4.ApiFileName(ScriptLanguage.Gsc));
        Assert.Empty(cod4.BundledDataFileNames);
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
    public void TheLineageIsInReleaseOrderAndTargetsThroughBo3()
    {
        // The after-BO3 shells live in a gitignored file, so the lineage may or may not reach past
        // BO3 depending on whether that file is present. What is guaranteed is that it starts at
        // CoD4, is release-ordered, and the last SUPPORTED game is BO3.
        Assert.Equal("cod4", GameProfile.All[0].ShortName);
        Assert.Equal("bo3", GameProfile.All.Last(static profile => profile.Supported).ShortName);

        for ( int i = 1; i < GameProfile.All.Length; i++ )
        {
            Assert.True(
                GameProfile.All[i].ReleaseYear >= GameProfile.All[i - 1].ReleaseYear,
                $"{GameProfile.All[i].ShortName} is out of release order");
        }
    }

    [Fact]
    public void SupportedGamesRunFromCod4ThroughBo3()
    {
        // Everything up to and including BO3 is targeted; everything after is left open.
        Assert.Equal(
            new[] { "cod4", "waw", "mw2", "bo1", "mw3", "bo2", "ghosts", "aw", "bo3" },
            GameProfile.All.Where(static profile => profile.Supported).Select(static profile => profile.ShortName).ToArray());

        Assert.All(
            GameProfile.All.Where(static profile => !profile.Supported),
            static profile => Assert.True(profile.ReleaseYear > 2015, $"{profile.ShortName} should be after BO3"));
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
        // The nice detail from the worksheet: WaW/BO1/BO2 shipped .csc, but not headers, classes,
        // the function keyword or #namespace.
        foreach ( string name in new[] { "waw", "bo1", "bo2" } )
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
    public void ForeachArrivesInMw2()
    {
        // CoD4 (2007) and WaW (2008) have only for/while; MW2 (2009) onward has foreach.
        Assert.False(GameProfile.ByName("cod4")!.HasForeach);
        Assert.False(GameProfile.ByName("waw")!.HasForeach);
        Assert.True(GameProfile.ByName("mw2")!.HasForeach);
        Assert.True(GameProfile.ByName("bo1")!.HasForeach);
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
        // #"..." a Treyarch feature (BO1, BO2, BO3); the Infinity Ward games have none.
        Assert.True(GameProfile.ByName("bo1")!.HasHashStrings);
        Assert.True(GameProfile.ByName("bo2")!.HasHashStrings);
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
