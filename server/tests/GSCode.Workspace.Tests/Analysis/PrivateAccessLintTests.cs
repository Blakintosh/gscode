using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Tests.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

public class PrivateAccessLintTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ScriptDatabase BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\util.gsc",
                "#namespace util;\nfunction private hidden()\n{\n}\nfunction shown()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, null, null, @"C:\bo3", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None).GetAwaiter().GetResult();

        return database;
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource, string askingPath = @$"{Raw}\scripts\main.gsc")
    {
        ScriptDatabase database = BuildWorkspace();
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(askingSource), NullInsertProvider.Instance, new NameTable());

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        return PrivateAccessLint.Analyze(
            result, database.Gsc, "raw", askingPath, builtins.For(ScriptLanguage.Gsc));
    }

    [Fact]
    public void CallingAPrivateFunctionFromAnotherFile_IsReported()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::hidden();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.PrivateFunctionNotVisible, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("util.gsc", diagnostic.Message);
    }

    [Fact]
    public void TheReport_PointsAtThePrivateDeclaration()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::hidden();\n}\n";

        DiagnosticRelation relation = Assert.Single(Assert.Single(Lint(source)).RelatedInformation);

        Assert.EndsWith("util.gsc", relation.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, relation.Range.Start.Line);
    }

    [Fact]
    public void CallingAPublicFunction_IsFine()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::shown();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void CallingAPrivateFunctionFromItsOwnFile_IsFine()
    {
        // Same path as the declaring file: privacy is per-file, not per-namespace.
        string source = "#namespace util;\nfunction private hidden()\n{\n}\nfunction run()\n{\n    hidden();\n}\n";

        Assert.Empty(Lint(source, @$"{Raw}\scripts\util.gsc"));
    }

    [Fact]
    public void UnknownFunction_IsNotReportedAsPrivate()
    {
        // "No such function" is a different problem and must not be mislabelled.
        string source = "#namespace game;\nfunction run()\n{\n    util::not_a_real_function();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void BuiltinCall_IsNeverReported()
    {
        string source = "#namespace game;\nfunction run()\n{\n    IPrintLn( \"hi\" );\n}\n";

        Assert.Empty(Lint(source));
    }
}
