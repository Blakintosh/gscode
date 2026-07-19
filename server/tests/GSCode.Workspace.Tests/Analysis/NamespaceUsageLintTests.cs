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

public class NamespaceUsageLintTests
{
    private const string Raw = @"C:\bo3\share\raw";

    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\scripts\util.gsc", "#namespace util;\nfunction helper()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
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

        return NamespaceUsageLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, askingPath);
    }

    [Fact]
    public void Warns_WhenQualifiedCallNamespaceIsNotImported()
    {
        string source = "#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.NamespaceNotImported, diagnostics[0].Code);
    }

    [Fact]
    public void NoWarning_WhenNamespaceIsImported()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NoWarning_ForOwnNamespaceOrUnqualifiedCalls()
    {
        string source = "#namespace util;\nfunction run()\n{\n    helper();\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Suppressed_WhenAUsingCannotBeResolved()
    {
        // The unresolved #using could contribute the namespace, so we must not flag it.
        string source = "#using scripts\\missing;\n#namespace game;\nfunction run()\n{\n    util::helper();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Empty(diagnostics);
    }
}
