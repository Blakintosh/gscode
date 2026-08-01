using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// The client (<c>.csc</c>) builtin libraries.
///
/// Only Black Ops 3 has a real one. WaW and BO1 have client scripts and no documentation describing
/// them, so theirs are DERIVED from their server libraries by the field-data tool: pruned to the names
/// there is evidence for, and corrected for the leading <c>localClientNum</c> the client VM takes.
/// None of that is documentation-verified and it cannot be — see the curated
/// <c>{prefix}_csc_functions.json</c>, which carries the standing caveat.
///
/// These pin the properties that make the derivation worth shipping, because it is generated data:
/// a change to the generator or a regeneration on a machine missing a source could quietly empty a
/// library or un-prune it, and nothing else would notice.
/// </summary>
public class ClientApiTests
{
    private static readonly string s_apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");

    private static BuiltinApi Client(GameProfile profile)
    {
        return ApiLoader.Load(s_apiDirectory, ScriptLanguage.Csc, profile);
    }

    private static BuiltinApi Server(GameProfile profile)
    {
        return ApiLoader.Load(s_apiDirectory, ScriptLanguage.Gsc, profile);
    }

    public static TheoryData<string> DerivedGames => new() { "waw", "bo1" };

    [Theory]
    [MemberData(nameof(DerivedGames))]
    public void AGameWithClientScripts_HasAClientLibrary(string shortName)
    {
        // The bug this exists for: the profile claimed HasClientScripts and listed the file in
        // BundledDataFileNames, but no such file was ever generated, so every .csc file in these two
        // games loaded BuiltinApi.Empty and offered no hover, signature help or completion at all.
        GameProfile profile = GameProfile.ByName(shortName)!;

        Assert.True(profile.HasClientScripts);
        Assert.NotEqual(0, Client(profile).Count);
    }

    [Theory]
    [MemberData(nameof(DerivedGames))]
    public void TheDerivedLibrary_IsPrunedFarBelowTheServerOne(string shortName)
    {
        // Most of a server library is not client-side, and shipping it whole would offer completions
        // for functions the client VM does not have. The prune keeps roughly a sixth of it; asserted
        // as a wide band because the exact count moves whenever the corpus evidence is re-harvested.
        GameProfile profile = GameProfile.ByName(shortName)!;
        int client = Client(profile).Count;
        int server = Server(profile).Count;

        Assert.InRange(client, 50, server / 2);
    }

    [Theory]
    [MemberData(nameof(DerivedGames))]
    public void TheClientIndexedNames_TakeLocalClientNumFirst(string shortName)
    {
        // The one systematic difference between the two VMs, and the whole reason the server library
        // cannot simply be copied. VisionSetNaked is the clearest case: the client form is
        // VisionSetNaked( 0, "vampire_low" ) against the server's VisionSetNaked( "vampire_low" ).
        BuiltinFunction? vision = Client(GameProfile.ByName(shortName)!).Find("VisionSetNaked");

        Assert.NotNull(vision);
        Assert.Equal("localClientNum", vision!.Overloads[0].Parameters[0].Name);
        Assert.True(vision.Overloads[0].Parameters[0].Mandatory);
    }

    [Theory]
    [MemberData(nameof(DerivedGames))]
    public void TheServerLibrary_KeepsTheServerSignature(string shortName)
    {
        // The correction belongs to the client library alone. Leaking it into the server one would
        // report every correct .gsc call as passing too few arguments.
        BuiltinFunction? vision = Server(GameProfile.ByName(shortName)!).Find("VisionSetNaked");

        Assert.NotNull(vision);
        Assert.NotEqual("localClientNum", vision!.Overloads[0].Parameters[0].Name);
    }

    [Fact]
    public void BlackOps3ClientLibrary_IsASourceRatherThanADerivation()
    {
        // t7_api_csc.json is hand-documented and is never generated or pruned by the field-data tool.
        // The proof is that it is not a subset of the server library the derivation would start from:
        // it carries hundreds of names that exist only on the client VM, which no amount of pruning a
        // GSC library could produce. This is why BO3 can be used to score the derivation.
        BuiltinApi client = Client(GameProfile.BlackOps3);
        BuiltinApi server = Server(GameProfile.BlackOps3);

        int clientOnly = client.All.Count(function => server.Find(function.Name) is null);

        Assert.True(
            clientOnly > 100,
            $"expected BO3's client library to carry many client-only names; found {clientOnly}");
    }

    [Fact]
    public void BlackOps3_DocumentsTheClientIndexItself()
    {
        // The fact the derivation is built on: BO3's own documentation names localClientNum first on
        // its client-indexed functions. If this ever stopped being true, the evidence the WaW and BO1
        // lists were scored against would be gone.
        BuiltinApi client = Client(GameProfile.BlackOps3);

        int indexed = client.All.Count(function =>
            function.Overloads.Any(overload =>
                overload.Parameters.Length > 0
                && overload.Parameters[0].Name.Contains("localClientNum", StringComparison.OrdinalIgnoreCase)));

        Assert.True(indexed > 150, $"expected BO3 to document many client-indexed functions; found {indexed}");
    }
}
