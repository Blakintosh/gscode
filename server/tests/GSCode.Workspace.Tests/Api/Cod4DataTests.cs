using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// CoD4 is the one non-BO3 game with bundled data — engine function names, radiant keys and entity
/// fields extracted from the mod-tools wordfile. These prove the data-file seam actually serves it:
/// loading with the CoD4 profile finds the wordfile's contents, and a game that ships no data
/// (MW2) loads empty rather than falling back to BO3's.
/// </summary>
public class Cod4DataTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;

    [Fact]
    public void Cod4_LoadsItsOwnBuiltinFunctions()
    {
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc, Cod4);

        Assert.True(api.Count > 500, $"expected the CoD4 function list, got {api.Count}");
        Assert.NotNull(api.Find("physicstrace"));
    }

    [Fact]
    public void Cod4_LoadsItsRadiantKeysAndFields()
    {
        ObjectFields fields = ObjectFields.Load(ApiDirectory, Cod4);

        // Radiant keys come from keys.txt, so they carry a type, not just a name.
        RadiantKey? targetName = fields.FindRadiantKey("targetname");
        Assert.NotNull(targetName);
        Assert.Equal("string", targetName!.Type);

        Assert.NotEmpty(fields.FindField("origin"));
    }

    [Fact]
    public void AGameWithoutData_LoadsEmpty()
    {
        // MW2 ships no data files, so it must not fall back to another game's builtins.
        Assert.Equal(0, ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc, Mw2).Count);
        Assert.Empty(Mw2.BundledDataFileNames);
    }

    [Fact]
    public void Cod4_ProfileDeclaresItsDataFiles()
    {
        Assert.Equal("cod4", Cod4.DataFilePrefix);
        Assert.Equal("cod4_api_gsc.json", Cod4.ApiFileName(ScriptLanguage.Gsc));
        // No client scripts, so no client API in the bundle.
        Assert.DoesNotContain("cod4_api_csc.json", Cod4.BundledDataFileNames);
        Assert.Contains("cod4_radiant_keys.json", Cod4.BundledDataFileNames);
    }
}
