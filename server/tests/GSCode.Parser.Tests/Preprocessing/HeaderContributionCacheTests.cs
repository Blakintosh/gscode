using GSCode.Parser.Preprocessing;
using Xunit;

namespace GSCode.Parser.Tests.Preprocessing;

/// <summary>
/// The header contribution cache replays what a header defined for the next file that inserts it,
/// so every test here is a pair of runs sharing one cache: the second run is the one that can be
/// handed the first run's answer.
/// </summary>
public class HeaderContributionCacheTests
{
    private const string BankPath = @"scripts\shared\bank.gsh";
    private const string ConfigPath = @"scripts\shared\config.gsh";

    /// <summary>The body of a macro as text, so a replayed definition can be told from a fresh one.</summary>
    private static string BodyText(PreprocessResult result, string name)
    {
        Assert.True(result.Macros.TryGet(name, out MacroDefinition definition), $"macro {name} is not defined");
        return string.Concat(definition.Body.Select(token => token.Text));
    }

    [Fact]
    public void MacroBankHeader_IsCachedAndReplayed_WithoutWalkingItAgain()
    {
        FakeHeaderMacroCache cache = new();

        FakeInsertProvider first = new FakeInsertProvider().AddInsert(BankPath, "#define CAP 5\n#define FLOOR 1");
        PreprocessResult firstResult = PreprocessTestHelper.Run($"#insert {BankPath};\nx = CAP;", first, cache);

        // Same path, different text: on disk it is one file, and the mismatch exists only so that a
        // replay can be told apart from a second walk.
        FakeInsertProvider second = new FakeInsertProvider().AddInsert(BankPath, "#define CAP 9\n#define FLOOR 9");
        PreprocessResult secondResult = PreprocessTestHelper.Run($"#insert {BankPath};\nx = CAP;", second, cache);

        Assert.Equal("5", BodyText(firstResult, "CAP"));
        Assert.Equal("5", BodyText(secondResult, "CAP"));
        Assert.Equal("1", BodyText(secondResult, "FLOOR"));
        Assert.True(cache.Contains(BankPath));
    }

    [Fact]
    public void HeaderWithConditional_IsNotCached_SoEachIncluderTakesItsOwnBranch()
    {
        const string source = """
            #if BUILD_LEVEL > 2
            #define FAST 1
            #else
            #define FAST 0
            #endif
            """;

        FakeHeaderMacroCache cache = new();

        // No BUILD_LEVEL here, so the condition is unresolvable and #else is taken. Nothing about
        // that walk is recorded - no invocation, no diagnostic - which is what made it cacheable.
        FakeInsertProvider first = new FakeInsertProvider().AddInsert(ConfigPath, source);
        PreprocessResult firstResult = PreprocessTestHelper.Run($"#insert {ConfigPath};", first, cache);
        Assert.Equal("0", BodyText(firstResult, "FAST"));

        FakeInsertProvider second = new FakeInsertProvider().AddInsert(ConfigPath, source);
        PreprocessResult secondResult = PreprocessTestHelper.Run(
            $"#define BUILD_LEVEL 5\n#insert {ConfigPath};", second, cache);

        Assert.Equal("1", BodyText(secondResult, "FAST"));
        Assert.False(cache.Contains(ConfigPath));
    }

    [Fact]
    public void HeaderWithConditional_IsNotCached_EvenWhenBothIncludersAgree()
    {
        const string source = """
            #if 1
            #define ON 1
            #endif
            """;

        FakeHeaderMacroCache cache = new();
        FakeInsertProvider provider = new FakeInsertProvider().AddInsert(ConfigPath, source);

        PreprocessTestHelper.Run($"#insert {ConfigPath};", provider, cache);

        // A constant condition is safe in principle, but "safe" is a property of the whole
        // condition after expansion, and the walk cannot tell one from a condition naming a macro
        // the includer failed to define. Refusing every conditional header is the conservative line.
        Assert.False(cache.Contains(ConfigPath));
    }

    [Fact]
    public void CachedHeader_NestedInsertResolvingElsewhere_IsWalkedAgainForThatFile()
    {
        const string outerSource = @"#insert scripts\shared.gsh;";
        const string outerResolved = @"c:\game\raw\scripts\outer.gsh";

        FakeHeaderMacroCache cache = new();

        // Raw's world: outer.gsh and the shared.gsh it inserts both come from raw.
        FakeInsertProvider raw = new FakeInsertProvider()
            .AddInsert(@"scripts\outer.gsh", outerSource, outerResolved)
            .AddInsert(@"scripts\shared.gsh", "#define OVERLAY 0", @"c:\game\raw\scripts\shared.gsh");
        PreprocessResult rawResult = PreprocessTestHelper.Run(@"#insert scripts\outer.gsh;", raw, cache);
        Assert.Equal("0", BodyText(rawResult, "OVERLAY"));

        // A mod overlays shared.gsh only, so outer.gsh resolves to the SAME file and hits the
        // cache, while the header it inserts is a different file entirely.
        FakeInsertProvider mod = new FakeInsertProvider()
            .AddInsert(@"scripts\outer.gsh", outerSource, outerResolved)
            .AddInsert(@"scripts\shared.gsh", "#define OVERLAY 1", @"c:\game\mods\m\scripts\shared.gsh");
        PreprocessResult modResult = PreprocessTestHelper.Run(@"#insert scripts\outer.gsh;", mod, cache);

        Assert.Equal("1", BodyText(modResult, "OVERLAY"));

        // The dependency edge has to name the mod's header too, or an edit to it invalidates nothing.
        InsertEdge nested = Assert.Single(modResult.Inserts, edge => edge.RawPath == @"scripts\shared.gsh");
        Assert.Equal(@"c:\game\mods\m\scripts\shared.gsh", nested.ResolvedPath);
    }

    [Fact]
    public void CachedHeader_NestedInsertResolvingTheSame_IsStillReplayed()
    {
        const string outerSource = @"#insert scripts\shared.gsh;";
        const string outerResolved = @"c:\game\raw\scripts\outer.gsh";
        const string sharedResolved = @"c:\game\raw\scripts\shared.gsh";

        FakeHeaderMacroCache cache = new();

        FakeInsertProvider first = new FakeInsertProvider()
            .AddInsert(@"scripts\outer.gsh", outerSource, outerResolved)
            .AddInsert(@"scripts\shared.gsh", "#define OVERLAY 0", sharedResolved);
        PreprocessTestHelper.Run(@"#insert scripts\outer.gsh;", first, cache);

        FakeInsertProvider second = new FakeInsertProvider()
            .AddInsert(@"scripts\outer.gsh", outerSource, outerResolved)
            .AddInsert(@"scripts\shared.gsh", "#define OVERLAY 0", sharedResolved);
        PreprocessResult secondResult = PreprocessTestHelper.Run(@"#insert scripts\outer.gsh;", second, cache);

        Assert.Equal("0", BodyText(secondResult, "OVERLAY"));

        // The nested header was never fetched, so the outer one was replayed rather than walked:
        // re-checking where a nested insert lands costs a resolve, not a walk, so a header carrying
        // nested inserts keeps the cache's win.
        Assert.DoesNotContain(@"scripts\shared.gsh", second.Fetched);
        Assert.Equal(2, secondResult.Inserts.Length);
        Assert.Contains(secondResult.Inserts, edge => edge.ResolvedPath == sharedResolved);
    }
}
