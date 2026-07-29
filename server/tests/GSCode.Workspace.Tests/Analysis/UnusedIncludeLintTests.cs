using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
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
/// The #include counterpart to <see cref="UnusedUsingLintTests"/>: an #include contributing nothing
/// this file calls is a greyed-out hint. Because #include is a merge dialect and the default indexer
/// runs as BO3 (which would not parse a bare function), the included file is analysed as CoD4 and
/// committed directly rather than indexed.
/// </summary>
public class UnusedIncludeLintTests
{
    private const string Raw = @"C:\bo3\share\raw";
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\common_scripts\utility.gsc", "helper()\n{\n}\n");

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();

        string utilityPath = @$"{Raw}\common_scripts\utility.gsc";
        ParseResult utility = ScriptAnalysis.Analyze(
            utilityPath, ScriptLanguage.Gsc, SourceText.From("helper()\n{\n}\n"), NullInsertProvider.Instance, new NameTable(), Cod4);
        database.Commit(utility, ResolutionContext.RawContext, isDirty: false, @"common_scripts\utility.gsc");

        return (database, resolver);
    }

    private static ImmutableArray<Diagnostic> Lint(string askingSource)
    {
        (ScriptDatabase database, PathResolver resolver) = BuildWorkspace();
        string askingPath = @$"{Raw}\scripts\main.gsc";
        ParseResult result = ScriptAnalysis.Analyze(
            askingPath, ScriptLanguage.Gsc, SourceText.From(askingSource), NullInsertProvider.Instance, new NameTable(), Cod4);

        return UnusedIncludeLint.Analyze(result, database.Gsc, ScriptLanguage.Gsc, resolver, askingPath);
    }

    [Fact]
    public void FlagsAnIncludeWhoseFunctionsAreNeverCalled()
    {
        Diagnostic hint = Assert.Single(Lint("#include common_scripts\\utility;\nrun()\n{\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
        Assert.Equal(DiagnosticSeverity.Hint, hint.Severity);
        Assert.Contains(DiagnosticTag.Unnecessary, hint.Tags);
        Assert.Contains("utility", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAnIncludeWhoseFunctionIsCalled()
    {
        Assert.Empty(Lint("#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n"));
    }

    [Fact]
    public void KeepsAnIncludeUsedByAPathCall()
    {
        // maps\...::helper is keyed (null, helper) too, so a path call counts as using it.
        Assert.Empty(Lint("#include common_scripts\\utility;\nrun()\n{\n\tcommon_scripts\\utility::helper();\n}\n"));
    }

    [Fact]
    public void SaysNothingWhenThereAreNoIncludes()
    {
        Assert.Empty(Lint("run()\n{\n\thelper();\n}\n"));
    }

    [Fact]
    public void AnUnresolvableIncludeSuppressesThePass()
    {
        // A missing target is UsingNotFound/UsingNotFound's job; this lint stays quiet rather than
        // guessing, so it never reports both a missing AND an unused include for the same line.
        Assert.Empty(Lint("#include scripts\\does_not_exist;\nrun()\n{\n}\n"));
    }
}
