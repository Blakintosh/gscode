using GSCode.Core;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// That the data a profile CLAIMS to ship is actually there.
///
/// <see cref="GameProfile.BundledDataFileNames"/> is derived from the profile's flags rather than from
/// the directory, so a profile can promise a file nobody ever generated and nothing notices. That is
/// not hypothetical: <c>HasClientScripts</c> put <c>waw_api_csc.json</c> and <c>bo1_api_csc.json</c> on
/// the list long before either existed, and the only symptom was that <c>.csc</c> files in those games
/// silently had no builtin support at all — <see cref="Workspace.Api.ApiLoader"/> returns an empty
/// library for a missing file, which looks exactly like a game with nothing to offer.
/// </summary>
public class BundledDataTests
{
    private static readonly string s_apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>
    /// Data a profile promises that is knowingly not shipped yet. Empty, and meant to stay that way —
    /// it briefly held WaW's and BO1's stock-script lists, which <c>StockScriptListTests</c> now
    /// generates.
    /// </summary>
    private static readonly string[] s_knownAbsent = [];

    public static TheoryData<string> ProfilesWithData
    {
        get
        {
            TheoryData<string> data = [];
            foreach ( GameProfile profile in GameProfile.All )
            {
                if ( profile.DataFilePrefix is not null )
                {
                    data.Add(profile.ShortName);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ProfilesWithData))]
    public void EveryPromisedDataFile_IsActuallyShipped(string shortName)
    {
        GameProfile profile = GameProfile.ByName(shortName)!;

        List<string> missing = [];
        foreach ( string fileName in profile.BundledDataFileNames )
        {
            if ( !s_knownAbsent.Contains(fileName) && !File.Exists(Path.Combine(s_apiDirectory, fileName)) )
            {
                missing.Add(fileName);
            }
        }

        Assert.True(missing.Count == 0, $"{shortName} promises data it does not ship: {string.Join(", ", missing)}");
    }

    [Fact]
    public void TheKnownAbsentList_DoesNotOutliveTheGap()
    {
        // An exclusion that has been fixed is an exclusion that hides the next regression, so the list
        // has to shrink when the files arrive rather than sit there being generous.
        foreach ( string fileName in s_knownAbsent )
        {
            Assert.False(
                File.Exists(Path.Combine(s_apiDirectory, fileName)),
                $"{fileName} now ships — remove it from s_knownAbsent so the invariant covers it");
        }
    }

    [Theory]
    [MemberData(nameof(ProfilesWithData))]
    public void TheStockScriptList_LoadsAndNamesRealScripts(string shortName)
    {
        // Existing is not the same as parsing. The format is bare lines with `#` comments, so a file
        // written with the wrong newline or left as a header would load as zero entries and the save
        // warning would go quiet again with nothing to show for it.
        GameProfile profile = GameProfile.ByName(shortName)!;
        Workspace.Api.StockScripts stock = Workspace.Api.StockScripts.Load(s_apiDirectory, profile);

        Assert.True(stock.Count > 500, $"{shortName} loaded only {stock.Count} stock scripts");

        // Every game in this repo's era ships this one, and it exercises the canonical form too:
        // the lists store forward slashes and lowercase, while the editor asks with whatever the
        // platform and the author used.
        Assert.True(
            stock.Contains(@"maps\_utility.gsc") || stock.Contains(@"scripts\shared\util_shared.gsc"),
            $"{shortName} has a stock list that does not contain its own utility script");
    }

    [Fact]
    public void AGameWithoutClientScripts_PromisesNoClientLibrary()
    {
        // The list is derived, so this is what stops a profile promising a file that would make no
        // sense to generate. CoD4 is the cross-check that .csc is a Treyarch thing, not an engine one.
        GameProfile cod4 = GameProfile.ByName("cod4")!;

        Assert.False(cod4.HasClientScripts);
        Assert.DoesNotContain("cod4_api_csc.json", cod4.BundledDataFileNames);
    }
}
