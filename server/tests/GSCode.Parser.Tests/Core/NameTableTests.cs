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
    public void PathUtil_NormalizeAbsolute_CanonicalizesCaseAndTrims()
    {
        // Case is folded only where the filesystem is case-insensitive. On Linux the cased
        // path is the real one — lowercasing it would name a file that does not exist.
        if ( OperatingSystem.IsWindows() )
        {
            Assert.Equal(@"c:\folders\some\path", PathUtil.NormalizeAbsolute(@"C:\Folders\Some\PATH\"));
        }
        else if ( OperatingSystem.IsMacOS() )
        {
            Assert.Equal("/folders/some/path", PathUtil.NormalizeAbsolute("/Folders/Some/PATH/"));
        }
        else
        {
            Assert.Equal("/Folders/Some/PATH", PathUtil.NormalizeAbsolute("/Folders/Some/PATH/"));
        }
    }

    [Fact]
    public void PathUtil_NormalizeScriptPath_ConvertsSlashes()
    {
        Assert.Equal(@"scripts\shared\util", PathUtil.NormalizeScriptPath("scripts/shared/UTIL"));
    }

    [Fact]
    public void PathUtil_IsUnder_RequiresSeparatorBoundary()
    {
        // IsUnder compares already-normalized paths, so it looks for the native separator.
        char sep = Path.DirectorySeparatorChar;
        string root = OperatingSystem.IsWindows() ? @"c:\root" : "/root";

        Assert.True(PathUtil.IsUnder($"{root}{sep}sub{sep}file.gsc", root));
        Assert.False(PathUtil.IsUnder($"{root}other{sep}file.gsc", root));
        Assert.False(PathUtil.IsUnder(root, root));
    }
}
