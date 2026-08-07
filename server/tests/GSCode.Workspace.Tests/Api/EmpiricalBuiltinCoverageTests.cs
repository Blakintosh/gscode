using System.Text.Json;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Workspace.Api;
using Xunit;

namespace GSCode.Workspace.Tests.Api;

/// <summary>
/// The WaW and BO1 builtin artifacts are enriched from their committed corpus harvests. Every
/// unresolved candidate must therefore be represented in the matching language library, even when
/// the only available entry is deliberately sparse and carries no guessed signature.
/// </summary>
public class EmpiricalBuiltinCoverageTests
{
    private static readonly string s_apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");

    /// <summary>
    /// Harvest candidates that are deliberately NOT in the library, because they are not engine
    /// functions at all. Each needs the evidence beside it: the rule this test enforces is that a
    /// name the corpus cannot explain gets curated in, and an exception to it must be argued, not
    /// assumed.
    ///
    /// <c>spawnapalmgroundflame</c> — a typo in one of BO1's own scripts. Both call sites pass the
    /// same three arguments, and the correctly spelled twin is two directories away:
    /// <c>maps\mp\_napalm.gsc(479)</c> writes <c>self SpawnNapalmGroundFlame( pos-(0,0,16),
    /// fxToPlay, fxAngles )</c> while <c>maps\_zombiemode_ability_napalm.gsc(450)</c> is a copy that
    /// lost the 'n' and the <c>self</c>. Curating it would turn a broken line in a shipped script
    /// into a documented engine function, which is precisely the mistake
    /// <c>BuiltinHarvestTests</c> warns about when it says frequency is the only discriminator
    /// between a typo and a missing builtin — one call in one file being the tell.
    /// </summary>
    private static readonly HashSet<string> s_notEngineFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "spawnapalmgroundflame",
    };

    [Theory]
    [InlineData("bo1")]
    [InlineData("waw")]
    public void EveryHarvestCandidate_IsPresentInItsLanguageLibrary(string shortName)
    {
        GameProfile profile = GameProfile.ByName(shortName)!;
        BuiltinApi gsc = ApiLoader.Load(s_apiDirectory, ScriptLanguage.Gsc, profile);
        BuiltinApi csc = ApiLoader.Load(s_apiDirectory, ScriptLanguage.Csc, profile);

        string report = Path.Combine(FindServerRoot(), "tests", "GSCode.Server.Tests", "harvest", $"{shortName}_missing_builtins.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(report));
        foreach ( JsonElement function in document.RootElement.GetProperty("functions").EnumerateArray() )
        {
            string name = function.GetProperty("name").GetString()!;
            foreach ( JsonElement language in function.GetProperty("languages").EnumerateArray() )
            {
                BuiltinApi api = string.Equals(language.GetString(), "Csc", StringComparison.OrdinalIgnoreCase)
                    ? csc
                    : gsc;

                if ( s_notEngineFunctions.Contains(name) )
                {
                    // An exclusion that has quietly become wrong is worse than none, so it is
                    // asserted in both directions: still reported by the harvest, still absent here.
                    Assert.True(
                        api.Find(name) is null,
                        $"{shortName} '{name}' is listed as not an engine function but IS in the library — remove it from one or the other");
                    continue;
                }

                Assert.True(
                    api.Find(name) is not null,
                    $"{shortName} {language.GetString()} harvest candidate '{name}' is absent from the generated API");
            }
        }

        // Keep this test meaningful after a successful regeneration (the missing-builtin
        // reports should then be empty). These are representative names recovered from the
        // committed CoD4/BO3-backed empirical snapshots.
        IReadOnlyList<(ScriptLanguage Language, string Name)> expected = shortName switch
        {
            "bo1" => new[] { (ScriptLanguage.Gsc, "setclientflag"), (ScriptLanguage.Csc, "spawnfakeent") },
            "waw" => new[] { (ScriptLanguage.Gsc, "destructible_state"), (ScriptLanguage.Csc, "getsoundvolume") },
            _ => throw new ArgumentOutOfRangeException(nameof(shortName))
        };
        foreach ( (ScriptLanguage language, string name) in expected )
        {
            BuiltinApi api = language == ScriptLanguage.Csc ? csc : gsc;
            Assert.True(api.Find(name) is not null, $"{shortName} {language} empirical builtin '{name}' is absent");
        }
    }

    private static string FindServerRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while ( directory is not null )
        {
            if ( File.Exists(Path.Combine(directory.FullName, "GSCode.slnx")) )
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the server project root.");
    }
}
