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

    /// <summary>
    /// A drive root is its OWN separator — <c>Path.GetFullPath</c> returns <c>C:\</c>, not
    /// <c>C:</c> — and trimming that trailing separator like any other path turns it into a
    /// DRIVE-RELATIVE path instead. <c>Path.GetFullPath("c:")</c> resolves against the current
    /// directory, not the drive root, so a workspace opened at a drive root silently normalized to
    /// somewhere else entirely; and <c>Path.Combine("c:", "share\\raw")</c> (as RootConfig does)
    /// produces the drive-relative <c>c:share\raw</c> rather than <c>c:\share\raw</c>.
    /// </summary>
    [Fact]
    public void PathUtil_NormalizeAbsolute_KeepsADriveRootRooted()
    {
        if ( !OperatingSystem.IsWindows() )
        {
            return;
        }

        string normalized = PathUtil.NormalizeAbsolute(@"C:\");

        Assert.Equal(@"c:\", normalized);
        Assert.True(Path.IsPathRooted(normalized));
    }

    /// <summary>The Linux counterpart: the filesystem root is `/`, and trimming its separator emptied it.</summary>
    [Fact]
    public void PathUtil_NormalizeAbsolute_KeepsTheFilesystemRootRooted()
    {
        if ( !OperatingSystem.IsLinux() )
        {
            return;
        }

        Assert.Equal("/", PathUtil.NormalizeAbsolute("/"));
    }

    /// <summary>
    /// <see cref="PathUtil.IsUnder"/>'s separator-boundary check assumed its directory argument
    /// never itself ends in a separator — true for every ordinary normalized path, but not for a
    /// normalized drive root, which is its own trailing separator.
    /// </summary>
    [Fact]
    public void PathUtil_IsUnder_AcceptsADriveRootDirectory()
    {
        if ( !OperatingSystem.IsWindows() )
        {
            return;
        }

        Assert.True(PathUtil.IsUnder(@"c:\raw\file.gsc", @"c:\"));
        Assert.False(PathUtil.IsUnder(@"c:\", @"c:\"));
    }
}
