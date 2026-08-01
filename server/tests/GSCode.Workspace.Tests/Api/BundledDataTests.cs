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
    /// The stock-script lists for WaW and BO1, which are promised and genuinely absent.
    ///
    /// They drive the warning about editing a file the game shipped, so without them that warning
    /// simply never fires for those two games — it degrades rather than breaks, which is why this is
    /// recorded as a gap instead of a failure. Generating them means enumerating each game's raw tree,
    /// as <c>cod4_stock_scripts.txt</c> and <c>t7_stock_scripts.txt</c> were. Delete from here when
    /// they are.
    /// </summary>
    private static readonly string[] s_knownAbsent = ["waw_stock_scripts.txt", "bo1_stock_scripts.txt"];

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
