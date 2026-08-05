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

    /// <summary>A hub declaring nothing of its own, reaching utility only by including it.</summary>
    private const string ChainSource = "#include common_scripts\\utility;\n";
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private static (ScriptDatabase Database, PathResolver Resolver) BuildWorkspace()
    {
        FakeFileSystem files = new FakeFileSystem()
            .AddFile(@$"{Raw}\common_scripts\utility.gsc", "helper()\n{\n}\n")
            .AddFile(@$"{Raw}\maps\_chain.gsc", ChainSource)
            .AddFile(@$"{Raw}\maps\_chain2.gsc", ChainSource);

        RootConfig config = RootConfig.Create(true, @"C:\bo3\share\raw", @"C:\bo3\mods", [], files);
        PathResolver resolver = new(config, files);
        ScriptDatabase database = new();

        string utilityPath = @$"{Raw}\common_scripts\utility.gsc";
        ParseResult utility = ScriptAnalysis.Analyze(
            utilityPath, ScriptLanguage.Gsc, SourceText.From("helper()\n{\n}\n"), NullInsertProvider.Instance, new NameTable(), Cod4);
        database.Commit(utility, ResolutionContext.RawContext, isDirty: false, @"common_scripts\utility.gsc");

        // A hub that declares nothing itself and exists only to pull utility in — the shape a
        // marginal test has to get right.
        ParseResult chain = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\_chain.gsc", ScriptLanguage.Gsc, SourceText.From(ChainSource),
            NullInsertProvider.Instance, new NameTable(), Cod4);
        database.Commit(chain, ResolutionContext.RawContext, isDirty: false, @"maps\_chain.gsc");

        ParseResult chain2 = ScriptAnalysis.Analyze(
            @$"{Raw}\maps\_chain2.gsc", ScriptLanguage.Gsc, SourceText.From(ChainSource),
            NullInsertProvider.Instance, new NameTable(), Cod4);
        database.Commit(chain2, ResolutionContext.RawContext, isDirty: false, @"maps\_chain2.gsc");

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
    public void KeepsAHubIncludedPurelyAsAConduit()
    {
        // The case that made this test marginal rather than direct. maps\_createpath.gsc reaches
        // flag_init through maps\_utility and includes nothing else; judging the directive by what
        // its TARGET declares called that unused and offered "Remove", and taking the offer broke the
        // file — 5026 then reports the call as out of scope. A Hint whose fix manufactures an Error
        // is worse than either rule being wrong on its own.
        Assert.Empty(Lint("#include maps\\_chain;\nrun()\n{\n\thelper();\n}\n"));
    }

    [Fact]
    public void StillFlagsAHubWhoseContentsAreReachedAnotherWay()
    {
        // The other half, and why membership in the closure is not enough: including a hub AND the
        // file beneath it is routine in the stock scripts, and there the hub really is redundant. On
        // CoD4 this distinction is 33 directives for maps\_utility alone.
        Diagnostic hint = Assert.Single(Lint(
            "#include maps\\_chain;\n#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n"));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, hint.Code);
        Assert.Contains("_chain", hint.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoConduitsCoveringEachOtherAreBothKept()
    {
        // The trap in judging each directive against the others: helper arrives through both chains,
        // so neither is the SOLE supplier and an independent test calls both removable. Each removal
        // is safe alone and the pair is not — and "Remove all N unused #include directives" takes the
        // pair. Measured against what is certainly kept instead, neither qualifies.
        Assert.Empty(Lint(
            "#include maps\\_chain;\n#include maps\\_chain2;\nrun()\n{\n\thelper();\n}\n"));
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
