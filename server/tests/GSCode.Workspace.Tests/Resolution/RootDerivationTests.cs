using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// Finding the game when nobody said where it is. The settings are authoritative, but requiring
/// them for the ordinary case — a mod living inside the install it targets — costs every user a
/// configuration step to reach the only behaviour they wanted. So an unconfigured root is derived
/// by walking up from the workspace folders looking for the install's own layout.
///
/// The reported regression is <see cref="ModInsideInstall_DerivesBothRoots"/>: opening
/// <c>&lt;install&gt;\mods\mp_redux</c> with nothing configured resolved no raw root, so every
/// #using missed, shared.gsh never loaded, and its macros then read as unknown functions — around
/// sixty diagnostics from one absent root.
/// </summary>
public class RootDerivationTests
{
    private const string Install = @"G:\games\bo3";
    private static GameProfile BlackOps3 => GameProfile.BlackOps3;
    private static GameProfile Cod4 => GameProfile.ByName("cod4")!;

    /// <summary>A BO3 install: raw under share\raw, and one mod beside it.</summary>
    private static FakeFileSystem BlackOps3Install()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Install}\share\raw\scripts\shared\util_shared.gsc")
            .AddFile(@$"{Install}\share\raw\scripts\shared\shared.gsh")
            .AddFile(@$"{Install}\mods\mp_redux\scripts\mp\killstreaks\_killstreaks.gsc");
    }

    private static RootConfig Derive(FakeFileSystem fileSystem, GameProfile profile, params string[] folders)
    {
        return RootConfig.Create(
            rawEnabled: true, rawPath: null, modsPath: null, workspaceFolders: folders,
            fileSystem: fileSystem, profile: profile);
    }

    [Fact]
    public void ModInsideInstall_DerivesBothRoots()
    {
        RootConfig config = Derive(BlackOps3Install(), BlackOps3, @$"{Install}\mods\mp_redux");

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\share\raw"), config.RawRoot);
        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\mods"), config.ModsRoot);
    }

    [Fact]
    public void InstallItselfOpen_DerivesBothRoots()
    {
        RootConfig config = Derive(BlackOps3Install(), BlackOps3, Install);

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\share\raw"), config.RawRoot);
        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\mods"), config.ModsRoot);
    }

    [Fact]
    public void RawFolderItselfOpen_StillFindsTheInstallAbove()
    {
        // Walking up passes through share\raw and share before reaching the install, and the probe
        // at each is share\raw beneath THAT folder - so this only works because the search does not
        // stop at the first ancestor.
        RootConfig config = Derive(BlackOps3Install(), BlackOps3, @$"{Install}\share\raw\scripts");

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\share\raw"), config.RawRoot);
    }

    [Fact]
    public void ModOutsideAnyInstall_StaysWorkspaceOnly()
    {
        // Nothing above C:\work looks like a game, and inventing a root here would be worse than
        // having none: it would resolve some files and silently mis-resolve others.
        FakeFileSystem fileSystem = new FakeFileSystem()
            .AddFile(@"C:\work\my_mod\scripts\main.gsc");

        RootConfig config = Derive(fileSystem, BlackOps3, @"C:\work\my_mod");

        Assert.Null(config.RawRoot);
        Assert.Null(config.ModsRoot);
    }

    [Fact]
    public void PreBo3Game_LooksForRawNotShareRaw()
    {
        // The layout is the dialect's answer: BO3 buries raw under share, CoD4 does not.
        FakeFileSystem fileSystem = new FakeFileSystem()
            .AddFile(@"C:\cod4\raw\maps\mp\_utility.gsc")
            .AddFile(@"C:\cod4\mods\mymod\maps\mp\_x.gsc");

        RootConfig config = Derive(fileSystem, Cod4, @"C:\cod4\mods\mymod");

        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\cod4\raw"), config.RawRoot);
        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\cod4\mods"), config.ModsRoot);
    }

    [Fact]
    public void Bo3LayoutIsNotFoundByAPreBo3Profile()
    {
        // The other half of the same claim - share\raw is not a raw folder to CoD4, so a BO3 tree
        // opened in the wrong game mode derives nothing rather than half-working.
        RootConfig config = Derive(BlackOps3Install(), Cod4, @$"{Install}\mods\mp_redux");

        Assert.Null(config.RawRoot);
    }

    [Fact]
    public void ConfiguredRawPath_BeatsWhatWouldBeDerived()
    {
        FakeFileSystem fileSystem = BlackOps3Install()
            .AddFile(@"D:\elsewhere\raw\scripts\shared\util_shared.gsc");

        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: @"D:\elsewhere\raw", modsPath: null,
            workspaceFolders: [@$"{Install}\mods\mp_redux"], fileSystem: fileSystem, profile: BlackOps3);

        Assert.Equal(PathUtil.NormalizeAbsolute(@"D:\elsewhere\raw"), config.RawRoot);
    }

    [Fact]
    public void ConfiguringRawAlone_StillFindsTheModsBesideIt()
    {
        // Half-configuring must not silently cost mod shadowing, which fails as a wrong ANSWER
        // rather than an error and so would be found only by noticing a stale definition.
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: @$"{Install}\share\raw", modsPath: null,
            workspaceFolders: [], fileSystem: BlackOps3Install(), profile: BlackOps3);

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\mods"), config.ModsRoot);
    }

    [Fact]
    public void RawDisabled_DerivesNothing()
    {
        RootConfig config = RootConfig.Create(
            rawEnabled: false, rawPath: null, modsPath: null,
            workspaceFolders: [@$"{Install}\mods\mp_redux"], fileSystem: BlackOps3Install(),
            profile: BlackOps3);

        Assert.Null(config.RawRoot);
        Assert.Null(config.ModsRoot);
    }

    [Fact]
    public void EarlierWorkspaceFolderWins_EvenWhenALaterOneMatchesSooner()
    {
        // Folder order is the user's choice, so it is exhausted before the next is tried rather
        // than losing to a shallower match under a folder they listed second.
        FakeFileSystem fileSystem = BlackOps3Install()
            .AddFile(@"D:\second\share\raw\scripts\shared\util_shared.gsc");

        RootConfig config = Derive(fileSystem, BlackOps3, @$"{Install}\mods\mp_redux", @"D:\second");

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Install}\share\raw"), config.RawRoot);
    }
}
