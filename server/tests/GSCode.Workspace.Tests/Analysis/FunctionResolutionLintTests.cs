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

/// <summary>
/// An unresolved call is reported against the one domain that could have explained it: a call that
/// names a script location explicitly cannot have meant a builtin, and an unqualified one could have
/// meant either.
/// </summary>
public class FunctionResolutionLintTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static ScriptDatabase BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(
                @$"{Raw}\scripts\util.gsc",
                "#namespace util;\nfunction shown()\n{\n}\nfunction private hidden()\n{\n}\n");

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
        return FunctionResolutionLint.Analyze(
            result, database.Gsc, "raw", askingPath, builtins.For(ScriptLanguage.Gsc), GameProfile.BlackOps3);
    }

    [Fact]
    public void AnUnqualifiedCallMatchingNothing_IsABuiltinMiss()
    {
        // Could have meant a script function or an engine one, and neither has it.
        string source = "#namespace vibing3;\nfunction main()\n{\n    BuiltInDoesNotExist();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.BuiltinFunctionNotFound, diagnostic.Code);
        // Error, not Warning: an unresolved call fails to link, so the script never loads.
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("BuiltInDoesNotExist", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AQualifiedCallIntoAnotherNamespace_IsAScriptMiss()
    {
        // ns::foo names a script location, so a builtin could never have explained it.
        string source = "#namespace vibing3;\nfunction main()\n{\n    other::scriptFunctionDoesNotExist();\n}\n";

        Diagnostic diagnostic = Assert.Single(Lint(source));

        Assert.Equal(GscDiagnosticCode.ScriptFunctionNotFound, diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("scriptFunctionDoesNotExist", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothKindsAreReportedTogether()
    {
        // The reported repro: one of each in the same function.
        string source = "#namespace vibing3;\nfunction main()\n{\n    BuiltInDoesNotExist();\n    other::scriptFunctionDoesNotExist();\n}\n";

        ImmutableArray<Diagnostic> diagnostics = Lint(source);

        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, static d => d.Code == GscDiagnosticCode.BuiltinFunctionNotFound);
        Assert.Contains(diagnostics, static d => d.Code == GscDiagnosticCode.ScriptFunctionNotFound);
    }

    [Fact]
    public void RealCallsAreNotReported()
    {
        // A known builtin, a resolvable qualified call, and a call to a function in this file.
        string source =
            "#using scripts\\util;\n#namespace vibing3;\nfunction main()\n{\n    IsDefined( 1 );\n    util::shown();\n    helper();\n}\nfunction helper()\n{\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void APrivateFunctionCountsAsExisting()
    {
        // "Exists but is private" is 5003's story; reporting it as missing too would contradict it.
        string source = "#using scripts\\util;\n#namespace vibing3;\nfunction main()\n{\n    util::hidden();\n}\n";

        Assert.Empty(Lint(source));
    }

    [Fact]
    public void AGameWithoutBuiltinData_ReportsNothing()
    {
        // Without an API library every builtin call would look unresolved, so the lint stands down.
        ScriptDatabase database = BuildWorkspace();
        string path = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc,
            SourceText.From("#namespace vibing3;\nfunction main()\n{\n    BuiltInDoesNotExist();\n}\n"),
            NullInsertProvider.Instance, new NameTable());

        ImmutableArray<Diagnostic> diagnostics = FunctionResolutionLint.Analyze(
            result, database.Gsc, "raw", path, BuiltinApi.Empty, GameProfile.ByName("waw")!);

        Assert.Empty(diagnostics);
    }
}
