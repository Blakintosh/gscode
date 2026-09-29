using GSCode.Core;

namespace GSCode.Testing;

/// <summary>
/// Puts <see cref="GameProfile.Active"/> on one game for as long as the scope lives, then puts
/// back whatever was there. Active is process-global and production code reads it in forty-odd
/// places, so a test that sets it and forgets to restore it breaks whichever class runs next.
///
/// Every test that changes the game goes through this, including the ones asking for the default:
/// the restore is what matters, not the select.
/// </summary>
public sealed class ProfileScope : IDisposable
{
    /// <summary>
    /// The game a test runs under when the game is not what it is testing. BO3, because it is what
    /// <see cref="GameProfile.Active"/> falls back to and what the generic test sources are written
    /// in (<c>function</c>, <c>#namespace</c>, <c>#using</c>).
    /// </summary>
    public static GameProfile Default => GameProfile.BlackOps3;

    private readonly GameProfile _previous;

    private ProfileScope(GameProfile profile)
    {
        _previous = GameProfile.Active;
        if ( !GameProfile.Select(profile.ShortName) )
        {
            // Select falls back to BO3 for a game nobody has implemented, which would run the test
            // under the wrong dialect while it believed it had the one it asked for.
            throw new InvalidOperationException($"{profile.ShortName} is not a supported profile.");
        }
    }

    /// <summary>Selects <paramref name="profile"/>, or <see cref="Default"/> when none is given.</summary>
    public static ProfileScope Use(GameProfile? profile = null)
    {
        return new ProfileScope(profile ?? Default);
    }

    public void Dispose()
    {
        GameProfile.Select(_previous.ShortName);
    }
}
