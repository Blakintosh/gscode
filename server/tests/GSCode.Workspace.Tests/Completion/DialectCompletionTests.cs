using GSCode.Core;
using GSCode.Workspace.Completion;
using Xunit;

namespace GSCode.Workspace.Tests.Completion;

/// <summary>
/// Completion offers only what the active dialect has. The keyword lists are shared, but
/// <see cref="GscKeywords.IsAvailable"/> filters them per profile (mirroring the lexer's keyword
/// gating), and the global objects come from <see cref="GameProfile.GlobalObjectNames"/>. So CoD4
/// is not offered BO3-only constructs, and BO3 keeps everything.
/// </summary>
public class DialectCompletionTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly GameProfile Bo3 = GameProfile.BlackOps3;

    [Theory]
    [InlineData("foreach")] // MW2+
    [InlineData("do")]      // BO3
    [InlineData("class")]   // BO3
    [InlineData("new")]     // BO3
    [InlineData("function")] // BO3
    [InlineData("#using")]  // BO3 import
    [InlineData("#namespace")]
    [InlineData("#insert")]
    [InlineData("#precache")]
    public void Cod4DoesNotOfferBlackOps3Constructs(string keyword)
    {
        Assert.False(GscKeywords.IsAvailable(keyword, Cod4));
        Assert.True(GscKeywords.IsAvailable(keyword, Bo3));
    }

    [Fact]
    public void Cod4OffersIncludeButBlackOps3DoesNot()
    {
        // #include is the Infinity Ward import; #using is BO3's.
        Assert.True(GscKeywords.IsAvailable("#include", Cod4));
        Assert.False(GscKeywords.IsAvailable("#include", Bo3));
    }

    [Theory]
    [InlineData("if")]
    [InlineData("for")]
    [InlineData("while")]
    [InlineData("return")]
    [InlineData("waittill")]
    [InlineData("const")]
    [InlineData("#define")]
    [InlineData("#if")]
    public void UniversalKeywordsAreOfferedEverywhere(string keyword)
    {
        Assert.True(GscKeywords.IsAvailable(keyword, Cod4));
        Assert.True(GscKeywords.IsAvailable(keyword, Bo3));
    }

    [Fact]
    public void GlobalObjectsComeFromTheProfile()
    {
        // self/level/game/anim are universal.
        Assert.Contains("self", Cod4.GlobalObjectNames);
        Assert.Contains("level", Cod4.GlobalObjectNames);
        Assert.Contains("anim", Cod4.GlobalObjectNames);

        // world (BO3+) and classes (BO3 class system) are not in the Infinity Ward line.
        Assert.Contains("world", Bo3.GlobalObjectNames);
        Assert.DoesNotContain("world", Cod4.GlobalObjectNames);
        Assert.Contains("classes", Bo3.GlobalObjectNames);
        Assert.DoesNotContain("classes", Cod4.GlobalObjectNames);
    }
}
