using GSCode.Core;
using GSCode.Core.Paths;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// What the cold index walks when the folder open in the editor is the game install, and devraw as
/// a second raw. The install is tools and assets around a few script folders — 295,640 files for
/// 1,105 scripts on BO3 — so only raw's script folder, devraw's and the mods are walked; BO3 keeps
/// every script under <c>scripts</c>, the earlier games across raw's top level. Devraw sits beside
/// raw, holds the same relative paths, and is searched after it, so a devraw file raw also has is
/// never what a lookup finds and is not indexed. Driven through the resolver and the watcher, the
/// two places that decide whether a file belongs in the index; the roots ARE the subject, so the
/// install paths are spelled here rather than taken from <c>TestPaths</c>.
/// </summary>
public class GameFolderScopeTests
{
    private const string Install = @"c:\bo3";
    private const string Raw = @"c:\bo3\share\raw";
    private const string DevRaw = @"c:\bo3\share\devraw";
    private const string Mods = @"c:\bo3\mods";

    private static FakeFileSystem Bo3Install()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util_shared.gsc", "#namespace util;\nfunction raw_version()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh")
            .AddFile(@$"{Raw}\maps\stray.gsc")
            .AddFile(@$"{Mods}\my_mod\scripts\mod_main.gsc")
            .AddFile(@$"{Install}\usermaps\zm_test\scripts\zm\zm_test.gsc")
            .AddFile(@$"{Install}\rex\templates\zm mod level\scripts\zm\template.gsc")
            .AddFile(@$"{Install}\share\assetconvert\generated.gsc");
    }

    private static PathResolver InstallOpened(FakeFileSystem files)
    {
        return new PathResolver(RootConfig.Create(true, null, null, [Install], files), files);
    }

    private static List<string> Targets(PathResolver resolver)
    {
        List<string> targets = [.. resolver.EnumerateIndexTargets()];
        targets.Sort(StringComparer.Ordinal);
        return targets;
    }

    [Fact]
    public void ABo3InstallOpened_WalksOnlyRawsScriptFolderAndTheMods()
    {
        PathResolver resolver = InstallOpened(Bo3Install());

        Assert.Equal(
            [
                PathUtil.NormalizeAbsolute(@$"{Mods}\my_mod\scripts\mod_main.gsc"),
                PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh"),
                PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"),
            ],
            Targets(resolver));
    }

    [Fact]
    public void RawOpenedOnItsOwn_IsTheSameScopeAsTheInstall()
    {
        FakeFileSystem files = Bo3Install();
        PathResolver resolver = new(RootConfig.Create(true, null, null, [Raw], files), files);

        Assert.DoesNotContain(PathUtil.NormalizeAbsolute(@$"{Raw}\maps\stray.gsc"), Targets(resolver));
        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"), Targets(resolver));
    }

    [Fact]
    public void ARawWithNoScriptFolder_IsWalkedWhole()
    {
        // A rawPath pointed at a loose folder of BO3 scripts — server/samples/bo3 is one — would
        // otherwise index nothing, silently.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@"c:\loose\a.gsc")
            .AddFile(@"c:\loose\nested\b.gsc");
        PathResolver resolver = new(RootConfig.Create(true, @"c:\loose", null, [], files), files);

        Assert.Equal([@"c:\loose\a.gsc", @"c:\loose\nested\b.gsc"], Targets(resolver));
    }

    [Fact]
    public void ACod4InstallOpened_WalksAllOfRaw_ButNothingElseInTheInstall()
    {
        // Every earlier game spreads its scripts across raw's top level.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@"c:\cod4\raw\maps\_utility.gsc")
            .AddFile(@"c:\cod4\raw\animscripts\combat.gsc")
            .AddFile(@"c:\cod4\mods\my_mod\maps\mp\gametypes\dm.gsc")
            .AddFile(@"c:\cod4\zone_source\leftover.gsc");

        using ProfileScope scope = ProfileScope.Use(GameProfile.Cod4);
        PathResolver resolver = new(RootConfig.Create(true, null, null, [@"c:\cod4"], files), files);

        Assert.Equal(
            [
                @"c:\cod4\mods\my_mod\maps\mp\gametypes\dm.gsc",
                @"c:\cod4\raw\animscripts\combat.gsc",
                @"c:\cod4\raw\maps\_utility.gsc",
            ],
            Targets(resolver));
    }

    [Fact]
    public void Devraw_IsFoundBesideRaw_OnlyWhenItExists()
    {
        FakeFileSystem without = Bo3Install();
        FakeFileSystem with = Bo3Install().AddFile(@$"{DevRaw}\scripts\dev_only.gsc");

        Assert.Null(RootConfig.Create(true, Raw, null, [], without).DevRawRoot);
        Assert.Equal(DevRaw, RootConfig.Create(true, Raw, null, [], with).DevRawRoot);
        Assert.Null(RootConfig.Create(false, Raw, null, [], with).DevRawRoot);
    }

    [Fact]
    public void Resolve_FindsRawFirst_AndDevrawOnlyForWhatRawLacks()
    {
        FakeFileSystem files = Bo3Install()
            .AddFile(@$"{DevRaw}\scripts\shared\util_shared.gsc")
            .AddFile(@$"{DevRaw}\scripts\dev_only.gsc");
        PathResolver resolver = InstallOpened(files);

        Assert.Equal(
            PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"),
            resolver.Resolve(ResolutionContext.RawContext, @"scripts\shared\util_shared.gsc"));
        Assert.Equal(
            PathUtil.NormalizeAbsolute(@$"{DevRaw}\scripts\dev_only.gsc"),
            resolver.Resolve(ResolutionContext.ForMod("my_mod"), @"scripts\dev_only.gsc"));
    }

    [Fact]
    public void ADevrawFile_IsRawContext_UnderItsOwnRelativePath_AndIndexedUnlessRawHasIt()
    {
        string shadowed = PathUtil.NormalizeAbsolute(@$"{DevRaw}\scripts\shared\util_shared.gsc");
        string devOnly = PathUtil.NormalizeAbsolute(@$"{DevRaw}\scripts\dev_only.gsc");
        FakeFileSystem files = Bo3Install().AddFile(shadowed).AddFile(devOnly);
        PathResolver resolver = InstallOpened(files);

        Assert.Equal(ResolutionContextKind.Raw, resolver.GetContext(devOnly).Kind);
        Assert.Equal(@"scripts\dev_only.gsc", resolver.GetScriptRelativePath(devOnly, ResolutionContext.RawContext));

        List<string> targets = Targets(resolver);
        Assert.Contains(devOnly, targets);
        Assert.DoesNotContain(shadowed, targets);
    }

    [Fact]
    public void IsIndexTarget_AgreesWithTheWalk()
    {
        FakeFileSystem files = Bo3Install().AddFile(@$"{DevRaw}\scripts\shared\util_shared.gsc");
        PathResolver resolver = InstallOpened(files);

        Assert.True(resolver.IsIndexTarget(@$"{Raw}\scripts\shared\util_shared.gsc"));
        Assert.True(resolver.IsIndexTarget(@$"{Mods}\my_mod\scripts\mod_main.gsc"));
        Assert.False(resolver.IsIndexTarget(@$"{Raw}\maps\stray.gsc"));
        Assert.False(resolver.IsIndexTarget(@$"{Install}\usermaps\zm_test\scripts\zm\zm_test.gsc"));
        Assert.False(resolver.IsIndexTarget(@$"{DevRaw}\scripts\shared\util_shared.gsc"));
        Assert.False(resolver.IsIndexTarget(@$"{Raw}\scripts\shared\readme.txt"));
    }

    [Fact]
    public async Task AScriptCreatedOutsideTheWalk_StaysOutOfTheIndex()
    {
        FakeFileSystem files = Bo3Install();
        PathResolver resolver = InstallOpened(files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        WatchedFileUpdater updater = new(database, indexer);

        string outside = @$"{Install}\usermaps\zm_test\scripts\zm\zm_new.gsc";
        string inside = @$"{Raw}\scripts\shared\new_shared.gsc";
        files.AddFile(outside).AddFile(inside);
        updater.Apply(outside, WatchedFileChange.Created);
        updater.Apply(inside, WatchedFileChange.Created);

        Assert.False(database.TryGetAnyRecord(PathUtil.NormalizeAbsolute(outside), out _));
        Assert.True(database.TryGetAnyRecord(PathUtil.NormalizeAbsolute(inside), out _));
    }

    [Fact]
    public async Task ARawFileComingAndGoing_SwapsTheDevrawCopyOutAndBackIn()
    {
        string raw = @$"{Raw}\scripts\shared\util_shared.gsc";
        string dev = PathUtil.NormalizeAbsolute(@$"{DevRaw}\scripts\shared\util_shared.gsc");
        FakeFileSystem files = Bo3Install().AddFile(dev, "#namespace util;\nfunction dev_version()\n{\n}\n");
        PathResolver resolver = InstallOpened(files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);
        WatchedFileUpdater updater = new(database, indexer);

        Assert.False(database.TryGetAnyRecord(dev, out _));

        files.RemoveFile(raw);
        updater.Apply(raw, WatchedFileChange.Deleted);
        Assert.True(database.TryGetAnyRecord(dev, out _));

        files.AddFile(raw, "#namespace util;\nfunction raw_version()\n{\n}\n");
        updater.Apply(raw, WatchedFileChange.Created);
        Assert.False(database.TryGetAnyRecord(dev, out _));
        Assert.True(database.TryGetAnyRecord(PathUtil.NormalizeAbsolute(raw), out _));
    }
}
