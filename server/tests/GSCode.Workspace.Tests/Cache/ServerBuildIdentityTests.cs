using GSCode.Workspace.Cache;
using Xunit;

namespace GSCode.Workspace.Tests.Cache;

/// <summary>
/// What the cache identity has to notice. A cached record is the finished analysis of a file, and
/// analysis depends on the dialect: the same source is a different program under CoD4 than under
/// BO3, because the keywords, the import style and the builtin set all differ. Restoring the wrong
/// game's records is therefore undetectable downstream — nothing about them looks damaged, they
/// simply describe another language — so the identity is the only place it can be caught.
/// </summary>
public class ServerBuildIdentityTests
{
    [Fact]
    public void TwoGamesNeverShareAnIdentity_EvenWithIdenticalDataFiles()
    {
        // The data files are the same list in both calls, which is the MW2 case made explicit:
        // a game bundling no data has nothing but the assembly MVIDs to distinguish it, so before
        // the game was part of the material its identity was whatever every other data-less game's
        // would be. Passing an empty list here is exactly that situation.
        string bo3 = ServerBuildIdentity.Compute([], "bo3");
        string mw2 = ServerBuildIdentity.Compute([], "mw2");

        Assert.NotEqual(bo3, mw2);
    }

    [Fact]
    public void TheSameGameAndDataGiveTheSameIdentity()
    {
        // Stability is the other half: if it changed between two calls in one build, the cache
        // would be wiped on every start and the whole warm path would silently never be taken.
        Assert.Equal(
            ServerBuildIdentity.Compute([], "cod4"),
            ServerBuildIdentity.Compute([], "cod4"));
    }

    [Fact]
    public void EverySupportedGameHasADistinctIdentity()
    {
        // Pairwise, so adding a game that collides with an existing one fails here rather than by
        // a user seeing another dialect's symbols restored into their workspace.
        string[] games = ["cod4", "waw", "mw2", "bo1", "bo3"];
        HashSet<string> identities = [];

        foreach ( string game in games )
        {
            Assert.True(
                identities.Add(ServerBuildIdentity.Compute([], game)),
                $"{game} shares its cache identity with another game");
        }
    }
}
