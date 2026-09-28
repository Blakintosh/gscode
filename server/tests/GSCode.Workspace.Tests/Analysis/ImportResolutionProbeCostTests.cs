using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// F8f: the filesystem probing behind the import lints (<see cref="FileImports"/>,
/// <see cref="UsingNotFoundLint"/>) WAS a real cost multiplier, confirmed here before it was
/// fixed. `UnusedUsingLint`'s own work (<see cref="UnusedUsingLint.Analyze"/>) is linear in
/// references and imports and was never the concern — see the class comment there.
///
/// The unbounded part was <see cref="PathResolver.Resolve"/>: it probes
/// <c>IFileSystem.FileExists</c> once per configured root until one hits, and
/// <see cref="FileImports.Resolve"/> (shared across four lints) and
/// <see cref="UsingNotFoundLint.Analyze"/> (independent, by design — see its own remarks) each
/// walked the same directive list once per file analysis with no memoization between them or
/// across analyses. On an adversarial workspace that measured as an exact 2x-by-caller,
/// 4x-by-root-count multiplier — see git history for the numbers before the fix landed.
///
/// <see cref="PathResolver.Resolve"/> now memoizes by (context, relative path), including a
/// miss, so what these tests assert changed shape: the FIRST resolution of a fresh file still
/// costs one probe per root per directive (a resolver that has never been asked cannot know the
/// answer without asking), but a SECOND caller reading the same resolver — which is exactly the
/// `UsingNotFoundLint` situation — now costs nothing. The residual, unavoidable root-count
/// multiplier on that cold cost is still asserted below, so a regression there is caught even
/// though the double-probe defect is gone. Wall-clock is not asserted: that needs a physical,
/// cold-cache filesystem and is a manual measurement, not a unit test.
/// </summary>
public class ImportResolutionProbeCostTests
{
    private const string Raw = @"C:\proj\raw";
    private const string Ws0 = @"C:\proj\ws0";
    private const string Ws1 = @"C:\proj\ws1";
    private const string Ws2 = @"C:\proj\ws2";

    private const int ResolvingCount = 20;
    private const int MissingCount = 20;

    /// <summary>
    /// Builds a workspace-context asking file whose roots are, in probe order,
    /// [Ws0 (its own folder), Ws1, Ws2, Raw] — four roots, per
    /// <see cref="PathResolver"/>'s documented workspace probe order. Half its <c>#using</c>
    /// directives resolve only in the LAST root; the other half resolve nowhere. Both are the
    /// expensive case: a hit in the last root pays every earlier root's miss, and a true miss
    /// pays every root there is.
    /// </summary>
    private static (CountingFileSystem FileSystem, PathResolver Resolver, string AskingPath, string Source)
        BuildAdversarialWorkspace()
    {
        FakeFileSystem files = new();
        System.Text.StringBuilder source = new();

        for ( int index = 0; index < ResolvingCount; index++ )
        {
            // Declares one function, never called by the asking file, so IsUsed would run to
            // completion for it too — not what these tests assert, but the realistic shape.
            files.AddFile(@$"{Raw}\scripts\lib{index}.gsc", $"#namespace lib{index};\nfunction helper()\n{{\n}}\n");
            source.Append($"#using scripts\\lib{index};\n");
        }

        for ( int index = 0; index < MissingCount; index++ )
        {
            source.Append($"#using scripts\\missing{index};\n");
        }

        string askingPath = @$"{Ws0}\main.gsc";
        files.AddFile(askingPath, "");

        CountingFileSystem counting = new(files);

        RootConfig config = RootConfig.Create(
            rawEnabled: true,
            rawPath: Raw,
            modsPath: @"C:\proj\mods-does-not-exist",
            workspaceFolders: [Ws0, Ws1, Ws2],
            fileSystem: counting);
        PathResolver resolver = new(config, counting);

        source.Append("#namespace game;\nfunction run()\n{\n}\n");

        return (counting, resolver, askingPath, source.ToString());
    }

    /// <summary>
    /// The baseline this test compares against: the same 40 directives, but with only ONE root to
    /// probe — the workspace folder the asking file lives in, with no siblings and raw disabled.
    /// Resolving targets sit in that one root directly, so every probe is a first-root hit or a
    /// single-root miss.
    /// </summary>
    private static (CountingFileSystem FileSystem, PathResolver Resolver, string AskingPath, string Source)
        BuildSingleRootWorkspace()
    {
        FakeFileSystem files = new();
        System.Text.StringBuilder source = new();

        for ( int index = 0; index < ResolvingCount; index++ )
        {
            files.AddFile(@$"{Ws0}\scripts\lib{index}.gsc", $"#namespace lib{index};\nfunction helper()\n{{\n}}\n");
            source.Append($"#using scripts\\lib{index};\n");
        }

        for ( int index = 0; index < MissingCount; index++ )
        {
            source.Append($"#using scripts\\missing{index};\n");
        }

        string askingPath = @$"{Ws0}\main.gsc";
        files.AddFile(askingPath, "");

        CountingFileSystem counting = new(files);

        // Raw disabled and no sibling folders: RootsFor(Workspace) yields exactly [Ws0].
        RootConfig config = RootConfig.Create(
            rawEnabled: false,
            rawPath: null,
            modsPath: null,
            workspaceFolders: [Ws0],
            fileSystem: counting);
        PathResolver resolver = new(config, counting);

        source.Append("#namespace game;\nfunction run()\n{\n}\n");

        return (counting, resolver, askingPath, source.ToString());
    }

    private static void ResolveImportsAndCheckUsingsExist(
        PathResolver resolver, string askingPath, string source)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        // Mirrors WorkspaceLints.Analyze exactly: FileImports.Resolve is shared across four lints
        // (called once), UsingNotFoundLint resolves the same directives again independently.
        LanguageStore store = new();
        FileImports.Resolve(result, store, ScriptLanguage.Gsc, resolver, askingPath);
        UsingNotFoundLint.Analyze(result, ScriptLanguage.Gsc, resolver, askingPath);
    }

    [Fact]
    public void FirstResolve_StillCostsOneProbePerRootPerDirective_TheColdCaseCannotBeAvoided()
    {
        // A resolver that has never been asked cannot know an answer without probing, so the
        // FIRST pass over a fresh file still walks every root for every directive — this is what
        // PathResolver.Resolve's memo does NOT and cannot remove. 40 directives × 4 roots.
        const int directives = ResolvingCount + MissingCount;
        const int roots = 4;
        const int expectedColdProbes = directives * roots;

        (CountingFileSystem fileSystem, PathResolver resolver, string askingPath, string source) =
            BuildAdversarialWorkspace();

        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
        FileImports.Resolve(result, new LanguageStore(), ScriptLanguage.Gsc, resolver, askingPath);

        Assert.Equal(expectedColdProbes, fileSystem.FileExistsCount);
    }

    [Fact]
    public void SecondResolve_OnTheSameResolver_IsFree_TheMemoKilledTheDoubleProbe()
    {
        // The defect this class exists to prove: before PathResolver.Resolve memoized, the SAME
        // directive list was walked twice per analysis — once by FileImports.Resolve (shared
        // across four lints) and independently again by UsingNotFoundLint, each paying every
        // root. Same resolver instance, same directives: the second walk must cost nothing.
        (CountingFileSystem fileSystem, PathResolver resolver, string askingPath, string source) =
            BuildAdversarialWorkspace();

        ResolveImportsAndCheckUsingsExist(resolver, askingPath, source);
        int afterBothCallers = fileSystem.FileExistsCount;

        const int directives = ResolvingCount + MissingCount;
        const int roots = 4;
        Assert.Equal(directives * roots, afterBothCallers);
    }

    [Fact]
    public void SingleRootBaseline_TheColdCostIsExactlyOneProbePerDirective()
    {
        const int directives = ResolvingCount + MissingCount;
        const int roots = 1;
        const int expectedColdProbes = directives * roots;

        (CountingFileSystem fileSystem, PathResolver resolver, string askingPath, string source) =
            BuildSingleRootWorkspace();

        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());
        FileImports.Resolve(result, new LanguageStore(), ScriptLanguage.Gsc, resolver, askingPath);

        Assert.Equal(expectedColdProbes, fileSystem.FileExistsCount);
    }

    [Fact]
    public void FourRootWorkspace_ColdCostIs4xTheSingleRootBaseline_RootCountStillMatters()
    {
        // The multiplier the memo does NOT remove, stated as a ratio: adding sibling workspace
        // folders (or a raw root) does not change WHAT is imported, only how many roots the FIRST
        // resolution of each import is checked against. This is the residual cost after the fix —
        // real, bounded by root count, and paid once per (context, relative path) rather than once
        // per caller per analysis.
        (CountingFileSystem adversarial, PathResolver adversarialResolver, string adversarialPath, string adversarialSource) =
            BuildAdversarialWorkspace();
        ResolveImportsAndCheckUsingsExist(adversarialResolver, adversarialPath, adversarialSource);

        (CountingFileSystem baseline, PathResolver baselineResolver, string baselinePath, string baselineSource) =
            BuildSingleRootWorkspace();
        ResolveImportsAndCheckUsingsExist(baselineResolver, baselinePath, baselineSource);

        Assert.Equal(4 * baseline.FileExistsCount, adversarial.FileExistsCount);
    }
}
