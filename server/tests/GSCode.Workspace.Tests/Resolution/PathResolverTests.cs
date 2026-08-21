using GSCode.Core.Paths;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Resolution;

/// <summary>
/// The resolver matrix: mod overlay shadowing, raw-only isolation, whole-root-open
/// classification, configured roots, and workspace-only (raw disabled / no configured root).
/// </summary>
public class PathResolverTests
{
    private const string InstallRoot = @"C:\bo3";
    private const string Raw = @"C:\bo3\share\raw";
    private const string Mods = @"C:\bo3\mods";

    private static FakeFileSystem StandardTree()
    {
        return new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util_shared.gsc")
            .AddFile(@$"{Raw}\scripts\shared\shared.gsh")
            .AddFile(@$"{Raw}\scripts\codescripts\struct.gsc")
            .AddFile(@$"{Mods}\mod_a\scripts\codescripts\struct.gsc")
            .AddFile(@$"{Mods}\mod_a\scripts\mod_only.gsc")
            .AddFile(@$"{Mods}\mod_b\scripts\b_only.gsc");
    }

    private static PathResolver StandardResolver(IFileSystem fileSystem, params string[] workspaceFolders)
    {
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: Raw, modsPath: Mods, workspaceFolders: workspaceFolders, fileSystem: fileSystem);

        return new PathResolver(config, fileSystem);
    }

    // --- Context classification ---

    [Theory]
    [InlineData(@"C:\bo3\share\raw\scripts\shared\util_shared.gsc")]
    [InlineData(@"C:\BO3\Share\Raw\Scripts\Shared\Util_Shared.gsc")]
    [InlineData(@"C:/bo3/share/raw/scripts/shared/util_shared.gsc")]
    [InlineData(@"C:\bo3\share\raw\scripts\..\scripts\shared\util_shared.gsc")]
    public void GetScriptRelativePath_NormalizesWhateverSpellingItIsGiven(string path)
    {
        // Every one of these names the same file, and an unnormalized spelling used to return "" --
        // silently, since the roots are normalized and IsUnder simply failed. That empty string
        // became ScriptRecord.RelativePath, and every import match downstream compared against it
        // and never fired, so a workspace behaved as though no file included anything.
        PathResolver resolver = StandardResolver(StandardTree());

        Assert.Equal(
            @"scripts\shared\util_shared.gsc",
            resolver.GetScriptRelativePath(path, resolver.GetContext(path)));
    }

    [Fact]
    public void GetScriptRelativePath_AFileOutsideEveryRoot_IsStillEmpty()
    {
        // The empty answer stays meaningful for the case it was written for: normalizing the input
        // must not make an unrelated file look like it sits under a root.
        PathResolver resolver = StandardResolver(StandardTree());
        string outside = @"D:\elsewhere\loose.gsc";

        Assert.Equal("", resolver.GetScriptRelativePath(outside, ResolutionContext.RawContext));
    }

    [Fact]
    public void GetContext_RawFile_ClassifiesRaw()
    {
        PathResolver resolver = StandardResolver(StandardTree());
        ResolutionContext context = resolver.GetContext(@$"{Raw}\scripts\shared\util_shared.gsc");

        Assert.Equal(ResolutionContextKind.Raw, context.Kind);
    }

    [Fact]
    public void GetContext_ModFile_ClassifiesModWithName()
    {
        PathResolver resolver = StandardResolver(StandardTree());
        ResolutionContext context = resolver.GetContext(@$"{Mods}\mod_a\scripts\mod_only.gsc");

        Assert.Equal(ResolutionContextKind.Mod, context.Kind);
        Assert.Equal("mod_a", context.ModName);
    }

    [Fact]
    public void GetContext_WholeInstallOpen_ModAndRawStillClassifyThemselves()
    {
        // The workspace IS the folder holding both roots: mods/raw prefixes win over the workspace match.
        PathResolver resolver = StandardResolver(StandardTree(), InstallRoot);

        Assert.Equal(ResolutionContextKind.Mod, resolver.GetContext(@$"{Mods}\mod_b\scripts\b_only.gsc").Kind);
        Assert.Equal(ResolutionContextKind.Raw, resolver.GetContext(@$"{Raw}\scripts\codescripts\struct.gsc").Kind);
    }

    [Fact]
    public void GetContext_FileOutsideEverything_AnchorsAtOwnDirectory()
    {
        PathResolver resolver = StandardResolver(StandardTree());
        ResolutionContext context = resolver.GetContext(@"C:\projects\my_mod\scripts\main.gsc");

        Assert.Equal(ResolutionContextKind.Workspace, context.Kind);
        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\projects\my_mod\scripts"), context.BaseFolder);
    }

    // --- Resolution + overlay shadowing ---

    [Fact]
    public void Resolve_ModContext_ModOverlayShadowsRaw()
    {
        PathResolver resolver = StandardResolver(StandardTree());
        ResolutionContext modA = ResolutionContext.ForMod("mod_a");

        string? resolved = resolver.Resolve(modA, @"scripts\codescripts\struct.gsc");

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Mods}\mod_a\scripts\codescripts\struct.gsc"), resolved);
    }

    [Fact]
    public void Resolve_ModContext_FallsBackToRaw()
    {
        PathResolver resolver = StandardResolver(StandardTree());

        string? resolved = resolver.Resolve(ResolutionContext.ForMod("mod_a"), @"scripts\shared\util_shared.gsc");

        Assert.Equal(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"), resolved);
    }

    [Fact]
    public void Resolve_ModContext_NeverSeesSiblingMod()
    {
        PathResolver resolver = StandardResolver(StandardTree());

        string? resolved = resolver.Resolve(ResolutionContext.ForMod("mod_a"), @"scripts\b_only.gsc");

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_RawContext_SeesRawOnly()
    {
        PathResolver resolver = StandardResolver(StandardTree());

        Assert.NotNull(resolver.Resolve(ResolutionContext.RawContext, @"scripts\shared\util_shared.gsc"));
        Assert.Null(resolver.Resolve(ResolutionContext.RawContext, @"scripts\mod_only.gsc"));
    }

    [Fact]
    public void Resolve_WorkspaceContext_WorkspaceFirstThenRaw()
    {
        FakeFileSystem fileSystem = StandardTree()
            .AddFile(@"C:\work\scripts\shared\util_shared.gsc");
        PathResolver resolver = StandardResolver(fileSystem, @"C:\work");

        string? resolved = resolver.Resolve(
            ResolutionContext.ForWorkspace(PathUtil.NormalizeAbsolute(@"C:\work")),
            @"scripts\shared\util_shared.gsc");

        Assert.Equal(PathUtil.NormalizeAbsolute(@"C:\work\scripts\shared\util_shared.gsc"), resolved);
    }

    [Fact]
    public void Resolve_ForwardSlashes_Work()
    {
        PathResolver resolver = StandardResolver(StandardTree());

        string? resolved = resolver.Resolve(ResolutionContext.RawContext, "scripts/shared/util_shared.gsc");

        Assert.NotNull(resolved);
    }

    [Theory]
    [InlineData(@"\scripts\foo.gsc")]
    [InlineData(@"C:\scripts\foo.gsc")]
    [InlineData(@"scripts\..\secrets.gsc")]
    [InlineData("")]
    public void Resolve_IllegalPaths_ReturnNull(string scriptPath)
    {
        PathResolver resolver = StandardResolver(StandardTree());

        Assert.Null(resolver.Resolve(ResolutionContext.RawContext, scriptPath));
    }

    // --- Configured roots + workspace-only mode ---

    [Fact]
    public void Create_RawPath_IsTakenVerbatim()
    {
        // The setting IS the root; nothing is derived from it and nothing competes with it. The
        // raw folder need not sit under the install, or anywhere near the workspace.
        FakeFileSystem fileSystem = new FakeFileSystem().AddFile(@"D:\customraw\scripts\foo.gsc");

        RootConfig config = RootConfig.Create(rawEnabled: true, rawPath: @"D:\customraw", modsPath: null, workspaceFolders: [], fileSystem: fileSystem);

        Assert.Equal(PathUtil.NormalizeAbsolute(@"D:\customraw"), config.RawRoot);
    }

    [Fact]
    public void Create_ConfiguredPathNotOnDisk_DropsToNull()
    {
        // A typo in the setting must degrade to workspace-only rather than producing a root that
        // resolves nothing: every lookup beneath it would miss, and the misses would read as the
        // user's scripts being wrong rather than the path being wrong.
        RootConfig config = RootConfig.Create(
            rawEnabled: true, rawPath: @"D:\nope", modsPath: @"D:\alsonope", workspaceFolders: [],
            fileSystem: new FakeFileSystem().AddFile(@"C:\work\scripts\main.gsc"));

        Assert.Null(config.RawRoot);
        Assert.Null(config.ModsRoot);
    }

    [Fact]
    public void Create_RawDisabled_NoRootsEvenWhenBothPathsAreSet()
    {
        // Explicit off beats explicit paths — the master switch is not advisory.
        RootConfig config = RootConfig.Create(rawEnabled: false, rawPath: Raw, modsPath: Mods, workspaceFolders: [@"C:\work"], fileSystem: StandardTree());

        Assert.Null(config.RawRoot);
        Assert.Null(config.ModsRoot);
        Assert.Single(config.WorkspaceFolders);
    }

    [Fact]
    public void Create_NoRawPath_WorkspaceOnlyMode()
    {
        RootConfig config = RootConfig.Create(rawEnabled: true, rawPath: null, modsPath: null, workspaceFolders: [@"C:\work"], fileSystem: new FakeFileSystem().AddFile(@"C:\work\scripts\main.gsc"));

        Assert.Null(config.RawRoot);
        Assert.Null(config.ModsRoot);
    }

    [Fact]
    public void Resolve_WorkspaceOnlyMode_SameCodeShorterChain()
    {
        FakeFileSystem fileSystem = new FakeFileSystem()
            .AddFile(@"C:\work\scripts\main.gsc")
            .AddFile(@$"{Raw}\scripts\raw_thing.gsc");

        RootConfig config = RootConfig.Create(rawEnabled: false, rawPath: null, modsPath: null, workspaceFolders: [@"C:\work"], fileSystem: fileSystem);
        PathResolver resolver = new(config, fileSystem);

        ResolutionContext context = resolver.GetContext(@"C:\work\scripts\main.gsc");

        Assert.NotNull(resolver.Resolve(context, @"scripts\main.gsc"));
        Assert.Null(resolver.Resolve(context, @"scripts\raw_thing.gsc"));
    }

    // --- Index target enumeration ---

    [Fact]
    public void EnumerateIndexTargets_CoversRawModsAndWorkspace_Deduplicated()
    {
        FakeFileSystem fileSystem = StandardTree().AddFile(@"C:\work\scripts\main.csc");
        PathResolver resolver = StandardResolver(fileSystem, @"C:\work", InstallRoot);

        List<string> targets = [.. resolver.EnumerateIndexTargets()];

        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"), targets);
        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh"), targets);
        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Mods}\mod_b\scripts\b_only.gsc"), targets);
        Assert.Contains(PathUtil.NormalizeAbsolute(@"C:\work\scripts\main.csc"), targets);
        Assert.Equal(targets.Count, new HashSet<string>(targets, StringComparer.Ordinal).Count);
    }

    /// <summary>
    /// A workspace folder that CONTAINS the raw root must not make the raw tree be walked twice.
    ///
    /// The result was always correct — a `seen` set deduplicated the paths — but the second walk
    /// still happened in full, and on a real install that tree is 160,382 files. So the assertion
    /// worth making is about the WALK, not the output: the file system is asked once.
    /// </summary>
    [Fact]
    public void EnumerateIndexTargets_WalksAContainedRootOnlyOnce()
    {
        CountingFileSystem fileSystem = new(StandardTree());

        // The workspace folder is the install root, which raw lives inside.
        PathResolver resolver = StandardResolver(fileSystem, InstallRoot, InstallRoot);

        List<string> targets = [.. resolver.EnumerateIndexTargets()];

        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"), targets);
        Assert.Equal(targets.Count, new HashSet<string>(targets, StringComparer.Ordinal).Count);
        Assert.Equal(1, fileSystem.EnumerationCount);
    }

    /// <summary>
    /// Every script extension comes back from ONE walk, not one walk per extension.
    ///
    /// The distinction is invisible in the results and is the entire point of the change: three
    /// extensions used to mean three full traversals of the same tree.
    /// </summary>
    [Fact]
    public void EnumerateIndexTargets_FindsEveryExtensionInOneWalkPerRoot()
    {
        CountingFileSystem fileSystem = new(StandardTree().AddFile(@$"{Raw}\scripts\shared\client.csc"));
        PathResolver resolver = StandardResolver(fileSystem);

        List<string> targets = [.. resolver.EnumerateIndexTargets()];

        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\util_shared.gsc"), targets);
        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\client.csc"), targets);
        Assert.Contains(PathUtil.NormalizeAbsolute(@$"{Raw}\scripts\shared\shared.gsh"), targets);

        // Raw and mods are two roots, so two walks — never two per extension.
        Assert.Equal(2, fileSystem.EnumerationCount);
    }

    /// <summary>Counts tree walks, so a test can assert on work done rather than on results.</summary>
    private sealed class CountingFileSystem : IFileSystem
    {
        private readonly FakeFileSystem _inner;

        public CountingFileSystem(FakeFileSystem inner)
        {
            _inner = inner;
        }

        public int EnumerationCount { get; private set; }

        public bool FileExists(string absolutePath)
        {
            return _inner.FileExists(absolutePath);
        }

        public bool DirectoryExists(string absolutePath)
        {
            return _inner.DirectoryExists(absolutePath);
        }

        public string ReadAllText(string absolutePath)
        {
            return _inner.ReadAllText(absolutePath);
        }

        public DateTime GetLastWriteTimeUtc(string absolutePath)
        {
            return _inner.GetLastWriteTimeUtc(absolutePath);
        }

        public IEnumerable<string> EnumerateFilesWithExtensions(
            string directory, System.Collections.Immutable.ImmutableArray<string> extensions)
        {
            EnumerationCount++;
            return _inner.EnumerateFilesWithExtensions(directory, extensions);
        }
    }
}
