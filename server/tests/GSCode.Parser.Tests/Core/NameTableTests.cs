using GSCode.Core;
using GSCode.Core.Paths;
using Xunit;

namespace GSCode.Parser.Tests.Core;

public class NameTableTests
{
    [Fact]
    public void Intern_SameText_ReturnsSameInstance()
    {
        NameTable table = new();

        string first = table.Intern("playerName".AsSpan());
        string second = table.Intern("playerName".AsSpan());

        Assert.Same(first, second);
    }

    [Fact]
    public void Intern_PreservesCase()
    {
        NameTable table = new();

        Assert.Equal("PlayerName", table.Intern("PlayerName".AsSpan()));
    }

    [Fact]
    public void InternLower_CanonicalizesToLowercase()
    {
        NameTable table = new();

        string canonical = table.InternLower("GetPlayerName".AsSpan());

        Assert.Equal("getplayername", canonical);
        Assert.Same(canonical, table.InternLower("GETPLAYERNAME".AsSpan()));
        Assert.Same(canonical, table.InternLower("getplayername".AsSpan()));
    }

    [Fact]
    public void PathUtil_NormalizeAbsolute_LowercasesAndTrims()
    {
        string normalized = PathUtil.NormalizeAbsolute(@"C:\Folders\Some\PATH\");

        Assert.Equal(@"c:\folders\some\path", normalized);
    }

    [Fact]
    public void PathUtil_NormalizeScriptPath_ConvertsSlashes()
    {
        Assert.Equal(@"scripts\shared\util", PathUtil.NormalizeScriptPath("scripts/shared/UTIL"));
    }

    [Fact]
    public void PathUtil_IsUnder_RequiresSeparatorBoundary()
    {
        Assert.True(PathUtil.IsUnder(@"c:\root\sub\file.gsc", @"c:\root"));
        Assert.False(PathUtil.IsUnder(@"c:\rootother\file.gsc", @"c:\root"));
        Assert.False(PathUtil.IsUnder(@"c:\root", @"c:\root"));
    }
}
