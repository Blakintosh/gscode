using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

public class UnusedUsingLintTests
{
    private const string Raw = @"C:\bo3\share\raw";

    /// <summary>
    /// A small world: util (plain functions), boot (an autoexec), shapes (a class), and
    /// util_more (a second contributor to the SAME namespace as util).
    /// </summary>
    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\util_more.gsc", "#namespace util;\nfunction extra()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\boot.gsc", "#namespace boot;\nfunction autoexec start()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\shapes.gsc", "#namespace shapes;\nclass Circle\n{\n}\n");

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        return (database, resolver);
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource)
    {
        (ScriptDatabase database, PathResolver resolver) = BuildWorkspace();
        string askingPath = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(askingSource), NullInsertProvider.Instance, new NameTable());

        return UnusedUsingLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, askingPath);
    }

    [Fact]
    public void Flags_ImportWhoseSymbolsAreNeverUsed()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.UnusedUsing, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Hint, diagnostic.Severity);
        Assert.Equal(DiagnosticTag.Unnecessary, Assert.Single(diagnostic.Tags));
        Assert.Equal(0, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void NoWarning_WhenAFunctionFromTheImportIsCalled()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void NoWarning_ForAutoexecOnlyImport()
    {
        // boot.gsc is imported purely for its side effects and references nothing.
        string source = "#using scripts\\boot;\n#namespace game;\nfunction run()\n{\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void NoWarning_WhenTheImportDeclaresAUsedClass()
    {
        string source = "#using scripts\\shapes;\n#namespace game;\nfunction run()\n{\n    c = new Circle();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void NoWarning_ForSiblingContributorToACalledNamespace()
    {
        // util_more does not declare helper(), but it contributes namespace util, and
        // namespace merging means the import may be what makes util:: resolvable.
        string source = "#using scripts\\util_more;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void Suppressed_WhenAUsingCannotBeResolved()
    {
        string source = "#using scripts\\missing;\n#using scripts\\util;\n#namespace game;\nfunction run()\n{\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void FlagsOnlyTheUnusedImport_WhenBothArePresent()
    {
        string source = "#using scripts\\util;\n#using scripts\\shapes;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        // shapes is on line 1 and contributes nothing used here.
        Assert.Equal(1, diagnostic.Range.Start.Line);
    }
}
