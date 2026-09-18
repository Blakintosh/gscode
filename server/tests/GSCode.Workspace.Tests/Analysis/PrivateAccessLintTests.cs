using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Paths;
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

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
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
    public void CallingAPrivateFunctionFromOutsideItsNamespace_IsReported()
    {
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::hidden();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.PrivateFunctionNotVisible, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        // The message names the namespace, since that is the unit privacy is scoped to.
        Assert.Contains("util", diagnostic.Message);
    }

    [Fact]
    public void CallingAPrivateFunctionFromAnotherFileInTheSameNamespace_IsFine()
    {
        // The core rule: private is scoped to the namespace, not the file. main.gsc declares
        // #namespace util, so util's private members are part of its own logical unit.
        string source = "#using scripts\\util;\n#namespace util;\nfunction run()\n{\n    util::hidden();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void UnqualifiedCallFromAnotherFileInTheSameNamespace_IsFine()
    {
        string source = "#using scripts\\util;\n#namespace util;\nfunction run()\n{\n    hidden();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void OneOfSeveralDeclaredNamespacesMatching_IsEnough()
    {
        // A file may declare several namespaces; matching any one of them grants access.
        string source = "#using scripts\\util;\n#namespace game;\nfunction a()\n{\n}\n#namespace util;\nfunction run()\n{\n    util::hidden();\n}\n";

        Assert.Empty(Lint(source));
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

    [Fact]
    public void APrivateCallAMacroExpandedInto_IsReported()
    {
        // A macro is not a way around `private`: the compiler sees the expansion, so a body that
        // reaches into another namespace's private function produces a call that does not link in
        // every file invoking it. The Error lands on the invocation, the only text on screen.
        string source =
            "#using scripts\\util;\n#define HELP() util::hidden()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.PrivateFunctionNotVisible, diagnostic.Code);
        Assert.Equal(5, diagnostic.Range.Start.Line);
    }

    [Fact]
    public void APrivateCallAMacroMakesTwice_IsReportedOnce()
    {
        string source =
            "#using scripts\\util;\n#define HELP() util::hidden(); util::hidden()\n#namespace game;\nfunction run()\n{\n    HELP();\n}\n";

        Assert.Single(Lint(source));
    }

    [Fact]
    public async Task ARelation_PointsAtTheHeaderWhenThePrivateFunctionArrivedThroughAnInsert()
    {
        // scripts\util.gsc does not write `hidden` itself — it #inserts a header that does. The
        // resulting FunctionSymbol's NameRange is a TRUE position in THAT header, not in
        // scripts\util.gsc, so the related-information path has to follow DeclaringPath there
        // too: pairing the header-true range with the including file's path pointed the relation
        // at whatever text happens to sit at that line and column in scripts\util.gsc — nothing
        // to do with where `hidden` is actually declared.
        string headerPath = @$"{Raw}\scripts\util_impl.gsh";
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(headerPath, "#namespace util;\nfunction private hidden()\n{\n}\n")
            .AddFile(@$"{Raw}\scripts\util.gsc", "#insert scripts\\util_impl.gsh;\nfunction shown()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();
        WorkspaceIndexer indexer = new(database, () => resolver, files, new NameTable());
        await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

        string askingPath = @$"{Raw}\scripts\main.gsc";
        string source = "#using scripts\\util;\n#namespace game;\nfunction run()\n{\n    util::hidden();\n}\n";
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable());

        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory);
        Diagnostic diagnostic = Assert.Single(PrivateAccessLint.Analyze(
            result, database.Gsc, "raw", askingPath, builtins.For(ScriptLanguage.Gsc)));

        DiagnosticRelation relation = Assert.Single(diagnostic.RelatedInformation);
        Assert.Equal(PathUtil.NormalizeAbsolute(headerPath), relation.FilePath, ignoreCase: true);
    }
}
