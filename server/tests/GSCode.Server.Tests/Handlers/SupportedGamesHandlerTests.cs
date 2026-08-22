using System.Text.Json;
using System.Text.RegularExpressions;
using GSCode.Core;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using Xunit;
using Xunit.Abstractions;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The roster behind the game picker, and the two lists it has to keep agreeing with.
///
/// This exists because the client used to own the list and it drifted: to nine games, four of them
/// cores with no dialect implemented, so picking one wrote a <c>gscode.game</c> value the setting's
/// own enum rejects and the server then resolved back to Black Ops III silently. Moving the list
/// to the server fixed the copy; nothing yet stopped the SETTING from drifting away from it, which
/// is the same bug one file over.
///
/// Joins <see cref="GameProfileCollection"/>: the roster and the selected game are both read off
/// <c>GameProfile.Active</c> and the supported set, which a sample or corpus run moves.
/// </summary>
[Collection(GameProfileCollection.Name)]
public class SupportedGamesHandlerTests
{
    private readonly ITestOutputHelper _output;

    public SupportedGamesHandlerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<SupportedGamesResponse> AskAsync()
    {
        return await new SupportedGamesHandler().Handle(new SupportedGamesParams(), CancellationToken.None);
    }

    /// <summary>Walks up from the test assembly to the client's manifest.</summary>
    private static string? FindClientManifest()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while ( directory is not null )
        {
            string candidate = Path.Combine(directory.FullName, "client", "package.json");
            if ( File.Exists(candidate) )
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Exactly the supported profiles, and nothing else. A core has no dialect filled in, so
    /// offering one is offering a game the server cannot actually parse as.
    /// </summary>
    [Fact]
    public async Task TheRosterIsExactlyTheSupportedProfiles()
    {
        SupportedGamesResponse response = await AskAsync();

        List<string> expected = [.. GameProfile.All.Where(p => p.Supported).Select(p => p.ShortName)];
        List<string> offered = [.. response.Games.Select(g => g.Id)];

        // Order included: the roster is in release order and the picker shows it as given, so a
        // reordering is a user-visible change rather than an implementation detail.
        Assert.Equal(expected, offered);
        Assert.NotEmpty(offered);
    }

    /// <summary>
    /// The selected game is what the server SELECTED, not what was asked for.
    ///
    /// An unrecognised name falls back to BO3, so the two differ exactly when something is wrong.
    /// A picker that ticks a game which is not in use rules out the very thing being looked for.
    /// </summary>
    [Fact]
    public async Task TheSelectedGameIsTheActiveProfile()
    {
        GameProfile previous = GameProfile.Active;
        try
        {
            GameProfile.Select("cod4");

            SupportedGamesResponse response = await AskAsync();

            Assert.Equal("cod4", response.SelectedGame);
            Assert.Equal(GameProfile.Cod4.DisplayName, response.SelectedDisplayName);

            // The tick has to land on something the picker is showing, or it lands on nothing.
            Assert.Contains(response.Games, game => game.Id == response.SelectedGame);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    /// <summary>
    /// A label separates two games that share a display name. Nothing does today, but the lineage
    /// holds two Modern Warfare 2s and two Modern Warfare 3s, and the year is what will tell them
    /// apart the moment a core is promoted.
    /// </summary>
    [Fact]
    public async Task EveryLabelCarriesItsReleaseYear()
    {
        SupportedGamesResponse response = await AskAsync();

        foreach (SupportedGame game in response.Games)
        {
            GameProfile profile = GameProfile.ByName(game.Id)!;
            Assert.Contains(profile.ReleaseYear.ToString(), game.Label, StringComparison.Ordinal);
        }

        Assert.Equal(response.Games.Count, response.Games.Select(g => g.Label).Distinct().Count());
    }

    /// <summary>
    /// The roster and the <c>gscode.game</c> enum are the same list.
    ///
    /// This is the assertion the original bug needed and did not have. The picker writes the id it
    /// was given straight into the setting, so an id the enum does not accept is a write VSCode
    /// marks invalid and the server resolves back to BO3 — reported nowhere the user is looking.
    /// The failure runs the other way too: a game in the enum but not the roster is one a user can
    /// select by hand and the picker will never offer.
    ///
    /// A cross-file check, because the bug lives in a gap no compiler relates.
    /// </summary>
    [Fact]
    public async Task TheRosterAndTheSettingEnumAreTheSameList()
    {
        string? manifestPath = FindClientManifest();
        if ( manifestPath is null )
        {
            _output.WriteLine("SKIPPED: client/package.json not found from the test output directory.");
            return;
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        List<string> enumValues = [];
        foreach ( JsonElement section in manifest.RootElement
            .GetProperty("contributes").GetProperty("configuration").EnumerateArray() )
        {
            if ( section.TryGetProperty("properties", out JsonElement properties)
                && properties.TryGetProperty("gscode.game", out JsonElement game)
                && game.TryGetProperty("enum", out JsonElement values) )
            {
                enumValues = [.. values.EnumerateArray().Select(v => v.GetString()!)];
            }
        }

        Assert.NotEmpty(enumValues);

        SupportedGamesResponse response = await AskAsync();
        List<string> offered = [.. response.Games.Select(g => g.Id)];

        _output.WriteLine($"roster: {string.Join(", ", offered)}");
        _output.WriteLine($"enum:   {string.Join(", ", enumValues)}");

        Assert.Equal(offered, enumValues);
    }

    /// <summary>
    /// The command is declared in the manifest. A command registered only in <c>extension.ts</c>
    /// does not appear in the palette at all, so the picker would exist and be unreachable — which
    /// is the state this whole feature was added to leave.
    /// </summary>
    [Fact]
    public void ThePickerCommandIsDeclaredInTheManifest()
    {
        string? manifestPath = FindClientManifest();
        if ( manifestPath is null )
        {
            _output.WriteLine("SKIPPED: client/package.json not found from the test output directory.");
            return;
        }

        string manifest = File.ReadAllText(manifestPath);
        using JsonDocument parsed = JsonDocument.Parse(manifest);

        List<string> commands = [.. parsed.RootElement.GetProperty("contributes").GetProperty("commands")
            .EnumerateArray().Select(c => c.GetProperty("command").GetString()!)];

        Assert.Contains("gscode.selectGame", commands);

        // And registered on the other side. A declared command with no handler throws
        // "command not found" when the palette runs it.
        string? extension = Path.Combine(Path.GetDirectoryName(manifestPath)!, "src", "extension.ts");
        Assert.True(File.Exists(extension));
        Assert.Matches(
            new Regex(@"registerCommand\(\s*""gscode\.selectGame"""),
            File.ReadAllText(extension));
    }
}
