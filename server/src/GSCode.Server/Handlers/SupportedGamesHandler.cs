using GSCode.Core;
using MediatR;
using OmniSharp.Extensions.JsonRpc;

namespace GSCode.Server.Handlers;

/// <summary>One game the extension can be switched to, as offered in a picker.</summary>
public sealed record SupportedGame(string Id, string Label);

/// <summary>
/// The games a picker may offer, in release order, and the one in force.
///
/// One list with two callers: <see cref="SupportedGamesHandler"/> answers the picker command, and
/// <see cref="TextSyncHandler"/> carries the same roster on gscode/gameMismatch so the offer to
/// switch needs no second round trip. Only the server knows which profiles are
/// <see cref="GameProfile.Supported"/>, and the client's own copy of this list had drifted to nine
/// games — four of them cores with no dialect filled in, so picking one wrote a value the
/// gscode.game enum does not accept and the server then resolved it back to Black Ops III.
/// </summary>
public static class GameRoster
{
    /// <summary>
    /// Exactly the supported profiles, which is also exactly what the gscode.game enum accepts —
    /// the two lists are the same list, so a pick can no longer write a setting the schema rejects.
    ///
    /// Labelled with the release year, since the display names alone do not separate the two Modern
    /// Warfare 2s or the two Modern Warfare 3s once the cores are ever promoted.
    /// </summary>
    public static List<SupportedGame> Supported()
    {
        List<SupportedGame> games = [];
        foreach ( GameProfile profile in GameProfile.All )
        {
            if ( profile.Supported )
            {
                games.Add(new SupportedGame(profile.ShortName, profile.DisplayName + " (" + profile.ReleaseYear + ")"));
            }
        }

        return games;
    }
}

/// <summary>Request for gscode/supportedGames. No parameters: the server knows its own roster.</summary>
[Method("gscode/supportedGames", Direction.ClientToServer)]
public sealed class SupportedGamesParams : IRequest<SupportedGamesResponse>
{
}

/// <summary>Response for gscode/supportedGames.</summary>
public sealed class SupportedGamesResponse
{
    /// <summary>The profile actually in force, as a short name (<c>bo3</c>).</summary>
    public string SelectedGame { get; set; } = "";

    /// <summary>That profile's display name, for a picker that wants to say what is current.</summary>
    public string SelectedDisplayName { get; set; } = "";

    /// <summary>Every game that may be picked, in release order.</summary>
    public IReadOnlyList<SupportedGame> Games { get; set; } = [];
}

/// <summary>
/// Answers the game picker: which games exist, and which one is running.
///
/// The selected game is <see cref="GameProfile.Active"/> rather than an echo of the client's
/// <c>gscode.game</c> setting, and the difference is the whole reason to ask the server at all. An
/// unrecognised name falls back to Black Ops III, so the setting says what was ASKED FOR while this
/// says what was SELECTED — and a picker that ticks a game which is not in use is worse than one
/// that ticks nothing, because it rules out the very thing that is wrong.
///
/// This is a request rather than a notification because the picker is user-driven and can be opened
/// at any time. gscode/gameMismatch pushes the same roster, but only when the server happens to
/// notice a file that does not look like the selected game, and only once per session.
/// </summary>
public sealed class SupportedGamesHandler : IJsonRpcRequestHandler<SupportedGamesParams, SupportedGamesResponse>
{
    public Task<SupportedGamesResponse> Handle(SupportedGamesParams request, CancellationToken cancellationToken)
    {
        GameProfile active = GameProfile.Active;

        return Task.FromResult(new SupportedGamesResponse
        {
            SelectedGame = active.ShortName,
            SelectedDisplayName = active.DisplayName,
            Games = GameRoster.Supported(),
        });
    }
}
