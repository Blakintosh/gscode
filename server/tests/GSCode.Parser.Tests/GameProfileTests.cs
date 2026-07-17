using GSCode.Core;
using Xunit;

namespace GSCode.Parser.Tests;

public class GameProfileTests
{
    [Fact]
    public void BlackOps3_Defaults_ExposeExpectedExtensionsAndGlobals()
    {
        GameProfile profile = GameProfile.BlackOps3;

        Assert.Equal("t7", profile.Id);
        Assert.Equal(".gsc", profile.ServerScriptExtension);
        Assert.Equal(".csc", profile.ClientScriptExtension);
        Assert.Equal(".gsh", profile.HeaderExtension);
        Assert.Contains("level", profile.GlobalObjectNames);
        Assert.Contains("self", profile.GlobalObjectNames);
    }
}
