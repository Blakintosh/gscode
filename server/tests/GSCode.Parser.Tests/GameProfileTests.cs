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
    public void OnlyBlackOps3IsVerified_TheRestAreShells()
    {
        // Every shell must stay a shell until someone confirms it, so this pins the intent: the
        // lineage is nameable, but only BO3's capabilities may be trusted.
        Assert.Single(GameProfile.All, static profile => profile.Verified);
        Assert.Same(GameProfile.BlackOps3, GameProfile.All.Single(static profile => profile.Verified));
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
