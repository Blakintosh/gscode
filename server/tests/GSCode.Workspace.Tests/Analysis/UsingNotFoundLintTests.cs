using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// A `#using` whose target does not exist.
///
/// `#insert` has always had this, because the preprocessor must read the file and notices when it
/// cannot. `#using` is resolved lazily and was never checked, so a typo produced no diagnostic at
/// all while failing to link at runtime — and silently switched off two other lints, both of
/// which abandon their pass when an import will not resolve.
///
/// It finds 15 real cases in the stock scripts, 10 of them the same missing `scripts\zm\_bb`,
/// which exists nowhere in the shipped tools.
/// </summary>
public class UsingNotFoundLintTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static (ImmutableArray<Diagnostic> Missing, ImmutableArray<Diagnostic> All) Lint(string source)
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util_shared.gsc", "#namespace util;\nfunction helper()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None)
            .GetAwaiter().GetResult();

        string path = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), GSCode.Parser.Preprocessing.NullInsertProvider.Instance, new NameTable());

        return (
            UsingNotFoundLint.Analyze(result, ScriptLanguage.Gsc, resolver, path),
            NamespaceUsageLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, path));
    }

    [Fact]
    public void ATargetThatExistsOnDiskButIsNotIndexedIsNotReported()
    {
        // The startup race: a #using target that exists on disk but has not been indexed yet must
        // NOT be flagged -- it links fine at runtime. The lint is fed an EMPTY database (nothing
        // indexed) and a resolver whose file system does have the file.
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\shared\util_shared.gsc", "#namespace util;\nfunction helper()\n{\n}\n");
        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);

        string path = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            path,
            ScriptLanguage.Gsc,
            SourceText.From("#using scripts\\shared\\util_shared;\nfunction run()\n{\n}\n"),
            GSCode.Parser.Preprocessing.NullInsertProvider.Instance,
            new NameTable());

        Assert.Empty(UsingNotFoundLint.Analyze(result, ScriptLanguage.Gsc, resolver, path));
    }

    [Fact]
    public void AMissingTargetIsAnError()
    {
        Diagnostic missing = Assert.Single(Lint("#using scripts\\nope;\nfunction run()\n{\n}\n").Missing);

        Assert.Equal(GscDiagnosticCode.UsingNotFound, missing.Code);
        // The script does not load. That is not a matter of taste.
        Assert.Equal(DiagnosticSeverity.Error, missing.Severity);
        Assert.Contains("nope", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APresentTargetIsFine()
    {
        Assert.Empty(Lint("#using scripts\\shared\\util_shared;\nfunction run()\n{\n}\n").Missing);
    }

    [Fact]
    public void EachMissingImportIsReportedSeparately()
    {
        Assert.Equal(2, Lint("#using scripts\\nope;\n#using scripts\\also_nope;\nfunction run()\n{\n}\n").Missing.Length);
    }

    [Fact]
    public void ItPointsAtThePathRatherThanTheWholeLine()
    {
        // The path is the part to fix, and it is what a quick-fix would replace.
        Diagnostic missing = Assert.Single(Lint("#using scripts\\nope;\nfunction run()\n{\n}\n").Missing);

        Assert.Equal(0, missing.Range.Start.Line);
        Assert.True(missing.Range.Start.Character > 0, "the range should start after the directive keyword");
    }

    [Fact]
    public void ThisIsWhyTheOtherLintsWentQuiet()
    {
        // The compounding half: NamespaceUsageLint abandons its pass on an unresolvable import,
        // so before this existed a single typo disabled namespace checking for the whole file
        // and left nothing to explain it. Both halves are asserted together so the relationship
        // is visible.
        (ImmutableArray<Diagnostic> missing, ImmutableArray<Diagnostic> namespaceLint) =
            Lint("#using scripts\\nope;\nfunction run()\n{\n    ghost::gone();\n}\n");

        Assert.Single(missing);
        Assert.Empty(namespaceLint);
    }
}
