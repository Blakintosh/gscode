using GSCode.Core;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// The cheap "does this file match the selected game" check. It keys off the import directive:
/// <c>#include</c> is pre-BO3, <c>#using</c>/<c>#namespace</c>/<c>#insert</c> are BO3. A wrong guess
/// only produces a dismissable prompt, so the tests pin the decisive cases, not every edge.
/// </summary>
public class GameShapeDetectorTests
{
    [Theory]
    [InlineData("#include common_scripts\\utility;\nmain() {}", GameShape.PreBlackOps3)]
    [InlineData("\t#include maps\\mp\\_util;\n", GameShape.PreBlackOps3)]
    [InlineData("#using scripts\\shared\\util_shared;\n", GameShape.BlackOps3)]
    [InlineData("#namespace foo;\nfunction f() {}", GameShape.BlackOps3)]
    [InlineData("#insert scripts\\shared\\shared.gsh;\n", GameShape.BlackOps3)]
    [InlineData("main() {\n\tx = 1;\n}\n", GameShape.Unknown)]
    public void Detect_ReadsTheImportDirective(string text, GameShape expected)
    {
        Assert.Equal(expected, GameShapeDetector.Detect(text));
    }

    [Fact]
    public void UsingAnimtree_IsNotMistakenForABlackOps3Using()
    {
        // #using_animtree exists in both families, so it must not read as a BO3 signal on its own.
        Assert.Equal(GameShape.Unknown, GameShapeDetector.Detect("#using_animtree( \"props\" );\nmain() {}"));
    }

    [Fact]
    public void Mismatches_WhenBo3ProfileMeetsAPreBo3File()
    {
        Assert.True(GameShapeDetector.Mismatches(GameProfile.BlackOps3, GameShape.PreBlackOps3));
        Assert.False(GameShapeDetector.Mismatches(GameProfile.BlackOps3, GameShape.BlackOps3));
    }

    [Fact]
    public void Mismatches_WhenPreBo3ProfileMeetsABo3File()
    {
        GameProfile cod4 = GameProfile.ByName("cod4")!;
        Assert.True(GameShapeDetector.Mismatches(cod4, GameShape.BlackOps3));
        Assert.False(GameShapeDetector.Mismatches(cod4, GameShape.PreBlackOps3));
    }

    [Fact]
    public void Mismatches_IsFalseForAnUnknownShape()
    {
        Assert.False(GameShapeDetector.Mismatches(GameProfile.BlackOps3, GameShape.Unknown));
        Assert.False(GameShapeDetector.Mismatches(GameProfile.ByName("cod4")!, GameShape.Unknown));
    }
}
