using GSCode.Core;
using GSCode.Core.Symbols;
using Xunit;

namespace GSCode.Parser.Tests;

public class GameProfileTests
{
    [Fact]
    public void BlackOps3_Defaults_ExposeExpectedExtensionsAndGlobals()
    {
        GameProfile profile = GameProfile.BlackOps3;

        Assert.Equal("t7", profile.Id);
        Assert.Equal(".gsc", profile.ServerScriptExtension);
        Assert.Equal(".csc", profile.ClientScriptExtension);
        Assert.Equal(".gsh", profile.HeaderExtension);
        Assert.Contains("level", profile.GlobalObjectNames);
        Assert.Contains("self", profile.GlobalObjectNames);
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
    public void Active_IsBlackOps3()
    {
        // The seam a dialect port turns into a per-workspace choice; today it is fixed.
        Assert.Same(GameProfile.BlackOps3, GameProfile.Active);
    }

    [Fact]
    public void TheLineageRunsFromCod4ToBo6InReleaseOrder()
    {
        Assert.Equal("cod4", GameProfile.All[0].ShortName);
        Assert.Equal("bo6", GameProfile.All[^1].ShortName);

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
        // Supported means "filled in and in scope"; Verified means "confirmed against real scripts".
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
    public void OnlyBlackOps3UsesAtSignScriptDoc()
    {
        Assert.Equal(ScriptDocStyle.AtSign, GameProfile.BlackOps3.ScriptDocStyle);
        Assert.All(
            GameProfile.All.Where(static profile => profile.ShortName != "bo3"),
            static profile => Assert.Equal(ScriptDocStyle.Hash, profile.ScriptDocStyle));
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
