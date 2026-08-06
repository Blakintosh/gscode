using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// CoD4's bundled data — engine function names, radiant keys and entity fields extracted from the
/// mod-tools wordfile. These prove the data-file seam actually serves it: loading with the CoD4
/// profile finds the wordfile's contents, and a profile that ships no data loads empty rather than
/// falling back to BO3's.
///
/// The dataless case is a CORE (MW3), not MW2. It used to be MW2, which is the better story: every
/// SUPPORTED game now ships a library, so the only profiles left to prove the no-fallback rule with
/// are the ones nobody has filled in yet.
/// </summary>
public class Cod4DataTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Mw2 = GameProfile.ByName("mw2")!;
    private static readonly GameProfile Mw3 = GameProfile.ByName("mw3")!;

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
        // A core ships no data files, so it must not fall back to another game's builtins.
        Assert.Null(Mw3.DataFilePrefix);
        Assert.Equal(0, ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc, Mw3).Count);
        Assert.Empty(Mw3.BundledDataFileNames);
    }

    [Fact]
    public void Mw2_LoadsItsOwnBuiltinFunctions()
    {
        // MW2's library is CoD4's shared pre-BO3 wordfile list plus what sweeping MW2's own scripts
        // proved it was missing, so both halves are asserted: a name only the wordfile has, and one
        // only the corpus found. Getting the second wrong is the failure that matters — it means the
        // empirical layer silently dropped out and 335 engine functions went back to looking unknown.
        BuiltinApi api = ApiLoader.Load(ApiDirectory, ScriptLanguage.Gsc, Mw2);

        Assert.True(api.Count > 1000, $"expected the MW2 function list, got {api.Count}");
        Assert.NotNull(api.Find("physicstrace"));
        Assert.NotNull(api.Find("setdvarifuninitialized"));
    }

    [Fact]
    public void Cod4_LoadsItsStockScriptList()
    {
        StockScripts stock = StockScripts.Load(ApiDirectory, Cod4);

        Assert.True(stock.Count > 500, $"expected the CoD4 stock list, got {stock.Count}");
        // A known stock script; slash style and case are normalized by the guard.
        Assert.True(stock.Contains(@"maps\_utility.gsc"));
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
