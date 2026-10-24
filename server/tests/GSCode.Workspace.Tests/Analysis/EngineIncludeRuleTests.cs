using System.Collections.Frozen;
using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Parser;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// How the engine finds a function on an include dialect: functions are keyed by the file that
/// declares them. A bare call looks in the calling file and in what its <c>#include</c>s merge in;
/// <c>maps\_foo::bar()</c> looks in maps\_foo. An <c>#include</c> merges the included file's OWN
/// functions, not those it got from its own includes, and a path call does not look through the
/// named file's includes.
///
/// So includes are one level deep, and a path call reaches the named file's own functions only. The
/// stock scripts agree in every file the game loads; the seven that rely on more
/// (maps\_createpath, _guide, _leapfrog, _mortarteam, _photosource, _documents, _wood) are named by
/// no other script and never compiled.
/// </summary>
public sealed class EngineIncludeRuleTests
{
    private static readonly GameProfile s_cod4 = GameProfile.Cod4;

    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static readonly TestFile[] s_files =
    [
        new TestFile(@"common_scripts\utility.gsc", "deep_helper( a )\n{\n}\n"),
        new TestFile(@"maps\_utility.gsc", "#include common_scripts\\utility;\n\nown_helper( b )\n{\n}\n"),
    ];

    private const string CallerPath = @"maps\caller.gsc";

    private static ImmutableArray<ResolvedFunction> Reached(string name, string pathCallTarget = "")
    {
        TestFile caller = new(CallerPath, "#include maps\\_utility;\n\nmain()\n{\n}\n");
        using TestWorkspace workspace = TestWorkspace.Build([.. s_files, caller], s_cod4);

        return DatabaseQueries.FunctionsTheCallReaches(
            workspace.Database.Gsc, "raw", TestPaths.Raw(CallerPath), null, name, pathCallTarget);
    }

    [Fact]
    public void ABareCall_SeesADirectIncludesOwnFunctions()
    {
        Assert.Equal("b", Assert.Single(Reached("own_helper")).Function.Parameters[0].Name);
    }

    [Fact]
    public void ABareCall_DoesNotSeeWhatAnIncludedFileIncludes()
    {
        Assert.Empty(Reached("deep_helper"));
    }

    [Fact]
    public void APathCall_SeesTheNamedFilesOwnFunctionsOnly()
    {
        Assert.Single(Reached("own_helper", @"maps\_utility"));
        Assert.Empty(Reached("deep_helper", @"maps\_utility"));
    }

    [Fact]
    public void APathCallToAFunctionTheNamedFileOnlyIncludes_IsUnresolved()
    {
        string source = "#include maps\\_utility;\n\nmain()\n{\n\tmaps\\_utility::deep_helper();\n}\n";
        using TestWorkspace workspace = TestWorkspace.Build(s_files, s_cod4);
        BuiltinApiSet builtins = BuiltinApiSet.Load(ApiDirectory, s_cod4);

        ImmutableArray<Diagnostic> diagnostics = FunctionResolutionLint.Analyze(
            workspace.Analyze(CallerPath, source), workspace.Database.Gsc, "raw", TestPaths.Raw(CallerPath),
            builtins.For(ScriptLanguage.Gsc), s_cod4, resolver: workspace.Resolver);

        Diagnostic reported = Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.ScriptFunctionNotFound, reported.Code);
    }

    [Fact]
    public void ABareCallToAFunctionAnIncludedFileOnlyIncludes_IsNotIncluded()
    {
        string source = "#include maps\\_utility;\n\nmain()\n{\n\tdeep_helper();\n}\n";
        using TestWorkspace workspace = TestWorkspace.Build(s_files, s_cod4);
        FrozenSet<string> engineNames = new[] { "println" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        ImmutableArray<Diagnostic> diagnostics = IncludeUsageLint.Analyze(
            workspace.Analyze(CallerPath, source), workspace.Database.Gsc, ScriptLanguage.Gsc, workspace.Resolver,
            TestPaths.Raw(CallerPath), engineNames, "raw", s_cod4);

        Diagnostic reported = Assert.Single(diagnostics);
        Assert.Equal(GscDiagnosticCode.FunctionNotIncluded, reported.Code);
        Assert.Contains(@"common_scripts\utility", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIncludeWhoseOwnFunctionsAreNeverCalled_IsUnused_WhateverItIncludes()
    {
        string source = "#include maps\\_utility;\n\nmain()\n{\n\tdeep_helper();\n}\n";
        using TestWorkspace workspace = TestWorkspace.Build(s_files, s_cod4);

        ImmutableArray<Diagnostic> diagnostics = UnusedIncludeLint.Analyze(
            workspace.Analyze(CallerPath, source), workspace.Database.Gsc, ScriptLanguage.Gsc,
            workspace.Resolver, TestPaths.Raw(CallerPath));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, Assert.Single(diagnostics).Code);
    }

    [Fact]
    public void AnIncludeUsedOnlyByPathCallsIntoIt_IsUnused()
    {
        // A path call loads the file it names itself; the include adds nothing to it.
        string source = "#include maps\\_utility;\n\nmain()\n{\n\tmaps\\_utility::own_helper();\n}\n";
        using TestWorkspace workspace = TestWorkspace.Build(s_files, s_cod4);

        ImmutableArray<Diagnostic> diagnostics = UnusedIncludeLint.Analyze(
            workspace.Analyze(CallerPath, source), workspace.Database.Gsc, ScriptLanguage.Gsc,
            workspace.Resolver, TestPaths.Raw(CallerPath));

        Assert.Equal(GscDiagnosticCode.UnusedInclude, Assert.Single(diagnostics).Code);
    }
}
