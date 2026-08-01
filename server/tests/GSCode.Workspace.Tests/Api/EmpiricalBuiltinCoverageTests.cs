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
