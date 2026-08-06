using System.Collections.Immutable;
using System.Linq;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Database;
using GSCode.Workspace.Resolution;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Include-graph scoping. A merge-dialect call is keyed by name only, so two files defining the same
/// function collapse to one key and go-to-definition would offer both. Scoping PREFERS the
/// definition in the asking file's include scope (itself + its <c>#include</c>d files); when nothing
/// is in scope it falls back to the full set, so a call still resolves while an <c>#include</c> is
/// missing.
/// </summary>
public class DialectIncludeScopeTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;
    private static readonly SymbolKey HelperKey = new(null, "helper", SymbolKind.Function);

    private static ParseResult AnalyzeIw(string path, string source)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable(), Cod4);
    }

    /// <summary>Two files each defining helper(); returns the definition set keyed (null, helper).</summary>
    private static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> TwoHelperDefinitions(ScriptDatabase database)
    {
        database.Commit(AnalyzeIw(@"c:\ws\common.gsc", "helper()\n{\n}\n"), ResolutionContext.RawContext, false, @"common_scripts\utility.gsc");
        database.Commit(AnalyzeIw(@"c:\ws\other.gsc", "helper()\n{\n}\n"), ResolutionContext.RawContext, false, @"unrelated\other.gsc");

        return [.. DatabaseQueries.FindReferences(database.Gsc, "raw", HelperKey)
            .Where(reference => reference.Entry.Kind == ReferenceKind.Definition)];
    }

    [Fact]
    public void IncludedScriptPathsReadsIncludeDirectives()
    {
        ParseResult main = AnalyzeIw(@"c:\ws\main.gsc", "#include common_scripts\\utility;\n#include maps\\mp\\_util;\nrun()\n{\n}\n");

        ImmutableArray<string> paths = DatabaseQueries.IncludedScriptPaths(main);

        Assert.Equal(2, paths.Length);
        Assert.Contains(@"common_scripts\utility", paths);
        Assert.Contains(@"maps\mp\_util", paths);
    }

    [Fact]
    public void TwoFilesWithTheSameNameBothMatchByKey()
    {
        // The premise: without scoping, a name-only key returns both definitions.
        ScriptDatabase database = new();
        Assert.Equal(2, TwoHelperDefinitions(database).Length);
    }

    [Fact]
    public void ScopingPrefersTheIncludedDefinition()
    {
        ScriptDatabase database = new();
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> both = TwoHelperDefinitions(database);

        ParseResult main = AnalyzeIw(@"c:\ws\main.gsc", "#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n");
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped = DatabaseQueries.PreferIncludeScope(
            both, @"scripts\main.gsc", DatabaseQueries.IncludedScriptPaths(main));

        (ScriptRecord Record, ReferenceEntry Entry) only = Assert.Single(scoped);
        Assert.Contains("common", only.Record.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopingFallsBackToAllWhenNothingIsIncluded()
    {
        ScriptDatabase database = new();
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> both = TwoHelperDefinitions(database);

        // main includes an unrelated file, so neither helper is in scope -> keep both.
        ParseResult main = AnalyzeIw(@"c:\ws\main.gsc", "#include scripts\\nothing;\nrun()\n{\n\thelper();\n}\n");
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped = DatabaseQueries.PreferIncludeScope(
            both, @"scripts\main.gsc", DatabaseQueries.IncludedScriptPaths(main));

        Assert.Equal(2, scoped.Length);
    }

    [Fact]
    public void APathCallPinsToItsNamedFile()
    {
        // foo() is defined in two files; a path call names exactly one of them.
        ScriptDatabase database = new();
        database.Commit(AnalyzeIw(@"c:\ws\a.gsc", "foo()\n{\n}\n"), ResolutionContext.RawContext, false, @"maps\a.gsc");
        database.Commit(AnalyzeIw(@"c:\ws\b.gsc", "foo()\n{\n}\n"), ResolutionContext.RawContext, false, @"maps\b.gsc");

        SymbolKey fooKey = new(null, "foo", SymbolKind.Function);
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> both = [.. DatabaseQueries
            .FindReferences(database.Gsc, "raw", fooKey)
            .Where(reference => reference.Entry.Kind == ReferenceKind.Definition)];
        Assert.Equal(2, both.Length);

        ParseResult main = AnalyzeIw(@"c:\ws\main.gsc", "run()\n{\n\tmaps\\a::foo();\n}\n");
        PathCallReference pathCall = Assert.Single(main.Extraction.PathCalls);

        // Pinning uses the named path as a one-file scope (no include set).
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped =
            DatabaseQueries.PreferIncludeScope(both, pathCall.Path, includedPaths: []);

        (ScriptRecord Record, ReferenceEntry Entry) only = Assert.Single(scoped);
        Assert.Equal(@"maps\a.gsc", only.Record.RelativePath);
    }

    [Fact]
    public void TheAskingFileItselfIsAlwaysInScope()
    {
        // A helper defined in the asking file wins even with no #include of it.
        Assert.True(DatabaseQueries.IsInIncludeScope(
            @"common_scripts\utility.gsc", @"common_scripts\utility.gsc", includedPaths: []));
    }

    // --- FunctionsInIncludeScope: what completion may offer bare, unqualified ---
    //
    // A merge dialect has no namespace, so `#namespace`-driven statement-scope completion (BO3)
    // finds nothing for it — every function it might offer bare is actually reachable through
    // `#include`, not a namespace block, and has to be gathered that way instead.

    [Fact]
    public void FunctionsInIncludeScope_OffersTheFilesOwnFunctions_WithNoIncludeAtAll()
    {
        ScriptDatabase database = new();
        database.Commit(AnalyzeIw(@"c:\ws\main.gsc", "run()\n{\n}\n"), ResolutionContext.RawContext, false, @"scripts\main.gsc");

        ImmutableArray<FunctionSymbol> functions = DatabaseQueries.FunctionsInIncludeScope(
            database.Gsc, "raw", @"c:\ws\main.gsc", includedPaths: []);

        Assert.Contains(functions, f => f.KeyName == "run");
    }

    [Fact]
    public void FunctionsInIncludeScope_OffersFunctionsFromAnIncludedFile()
    {
        ScriptDatabase database = new();
        database.Commit(
            AnalyzeIw(@"c:\ws\utility.gsc", "helper()\n{\n}\n"), ResolutionContext.RawContext, false, @"common_scripts\utility.gsc");
        database.Commit(AnalyzeIw(@"c:\ws\main.gsc", "run()\n{\n}\n"), ResolutionContext.RawContext, false, @"scripts\main.gsc");

        ImmutableArray<string> includedPaths = DatabaseQueries.IncludedScriptPaths(
            AnalyzeIw(@"c:\ws\main.gsc", "#include common_scripts\\utility;\nrun()\n{\n}\n"));

        ImmutableArray<FunctionSymbol> functions = DatabaseQueries.FunctionsInIncludeScope(
            database.Gsc, "raw", @"c:\ws\main.gsc", includedPaths);

        Assert.Contains(functions, f => f.KeyName == "helper");
        Assert.Contains(functions, f => f.KeyName == "run");
    }

    [Fact]
    public void FunctionsInIncludeScope_DoesNotOfferFunctionsFromAnUnincludedFile()
    {
        ScriptDatabase database = new();
        database.Commit(
            AnalyzeIw(@"c:\ws\utility.gsc", "helper()\n{\n}\n"), ResolutionContext.RawContext, false, @"common_scripts\utility.gsc");
        database.Commit(AnalyzeIw(@"c:\ws\main.gsc", "run()\n{\n}\n"), ResolutionContext.RawContext, false, @"scripts\main.gsc");

        ImmutableArray<FunctionSymbol> functions = DatabaseQueries.FunctionsInIncludeScope(
            database.Gsc, "raw", @"c:\ws\main.gsc", includedPaths: []);

        Assert.DoesNotContain(functions, f => f.KeyName == "helper");
    }

    // --- The namespace dialect needs the SAME narrowing, for a different reason ---
    //
    // A merge dialect drops the namespace from the key, so same-named functions collapse. BO3
    // keeps the namespace, but a namespace is not unique to a FILE: the stock scripts declare
    // `#namespace globallogic_utils` in both `scripts\mp\gametypes\_globallogic_utils.gsc` and
    // `scripts\zm\gametypes\_globallogic_utils.gsc`. So `globallogic_utils::func` names two
    // declarations, and only the asking file's `#using` list says which one it means.

    private static readonly GameProfile Bo3 = GameProfile.ByName("bo3")!;

    private static ParseResult AnalyzeBo3(string path, string source)
    {
        return ScriptAnalysis.Analyze(
            path, ScriptLanguage.Gsc, SourceText.From(source), NullInsertProvider.Instance, new NameTable(), Bo3);
    }

    /// <summary>The MP and ZM copies of one namespace, each declaring the same function.</summary>
    private static ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> TwoNamespaceCopies(ScriptDatabase database)
    {
        const string source = "#namespace globallogic_utils;\nfunction get_time_remaining()\n{\n}\n";

        database.Commit(
            AnalyzeBo3(@"c:\raw\mp_utils.gsc", source),
            ResolutionContext.RawContext, false, @"scripts\mp\gametypes\_globallogic_utils.gsc");
        database.Commit(
            AnalyzeBo3(@"c:\raw\zm_utils.gsc", source),
            ResolutionContext.RawContext, false, @"scripts\zm\gametypes\_globallogic_utils.gsc");

        SymbolKey key = new("globallogic_utils", "get_time_remaining", SymbolKind.Function);
        return [.. DatabaseQueries.FindReferences(database.Gsc, "raw", key)
            .Where(reference => reference.Entry.Kind == ReferenceKind.Definition)];
    }

    [Fact]
    public void TwoFilesSharingANamespaceBothMatchByKey()
    {
        // The premise: the namespace is in the key and STILL does not separate them.
        ScriptDatabase database = new();
        Assert.Equal(2, TwoNamespaceCopies(database).Length);
    }

    [Fact]
    public void ImportScopePrefersTheUsedNamespaceCopy()
    {
        ScriptDatabase database = new();
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> both = TwoNamespaceCopies(database);

        // _globallogic_spawn.gsc uses only the ZM copy, so the MP copy is not reachable from it.
        ParseResult spawn = AnalyzeBo3(
            @"c:\raw\spawn.gsc",
            "#using scripts\\zm\\gametypes\\_globallogic_utils;\n"
            + "function run()\n{\n    globallogic_utils::get_time_remaining();\n}\n");

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped = DatabaseQueries.PreferIncludeScope(
            both, @"scripts\zm\gametypes\_globallogic_spawn.gsc", DatabaseQueries.LinkedScriptPaths(spawn, Bo3));

        (ScriptRecord Record, ReferenceEntry Entry) only = Assert.Single(scoped);
        Assert.Equal(@"scripts\zm\gametypes\_globallogic_utils.gsc", only.Record.RelativePath);
    }

    [Fact]
    public void ImportScopeFallsBackToBothWhenNeitherIsUsed()
    {
        // A missing `#using` must not make go-to-definition dead-end: offer both while it is fixed.
        ScriptDatabase database = new();
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> both = TwoNamespaceCopies(database);

        ParseResult spawn = AnalyzeBo3(
            @"c:\raw\spawn.gsc", "function run()\n{\n    globallogic_utils::get_time_remaining();\n}\n");

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> scoped = DatabaseQueries.PreferIncludeScope(
            both, @"scripts\zm\gametypes\_globallogic_spawn.gsc", DatabaseQueries.LinkedScriptPaths(spawn, Bo3));

        Assert.Equal(2, scoped.Length);
    }

    // --- LinkedScriptPaths: the one place the dialect fork lives ---

    [Fact]
    public void LinkedScriptPathsReadsUsingDirectivesOnANamespaceDialect()
    {
        ParseResult spawn = AnalyzeBo3(
            @"c:\raw\spawn.gsc",
            "#using scripts\\zm\\gametypes\\_globallogic_utils;\n#using scripts\\shared\\util_shared;\n"
            + "function run()\n{\n}\n");

        ImmutableArray<string> paths = DatabaseQueries.LinkedScriptPaths(spawn, Bo3);

        Assert.Equal(2, paths.Length);
        Assert.Contains(@"scripts\zm\gametypes\_globallogic_utils", paths);
        Assert.Contains(@"scripts\shared\util_shared", paths);
    }

    [Fact]
    public void LinkedScriptPathsReadsIncludeDirectivesOnAMergeDialect()
    {
        ParseResult main = AnalyzeIw(@"c:\ws\main.gsc", "#include common_scripts\\utility;\nrun()\n{\n}\n");

        ImmutableArray<string> paths = DatabaseQueries.LinkedScriptPaths(main, Cod4);

        Assert.Equal(@"common_scripts\utility", Assert.Single(paths));
    }

    [Fact]
    public void LinkedScriptPathsIgnoresTheOtherDialectsDirective()
    {
        // The failure this guards: asking for the wrong list returns EMPTY rather than throwing,
        // and an empty scope silently falls back to the unnarrowed set.
        ParseResult usingOnly = AnalyzeBo3(
            @"c:\raw\spawn.gsc", "#using scripts\\shared\\util_shared;\nfunction run()\n{\n}\n");

        Assert.Empty(DatabaseQueries.LinkedScriptPaths(usingOnly, Cod4));
        Assert.Single(DatabaseQueries.LinkedScriptPaths(usingOnly, Bo3));
    }

    // --- ScopeToIncludeGraph: the same collision, in the reference COUNT ---

    [Fact]
    public void ReferenceScopingSeparatesTheTwoNamespaceCopies()
    {
        ScriptDatabase database = new();
        TwoNamespaceCopies(database);

        // A caller that uses only the ZM copy. Its call must count against ZM, not MP.
        database.Commit(
            AnalyzeBo3(
                @"c:\raw\spawn.gsc",
                "#using scripts\\zm\\gametypes\\_globallogic_utils;\n"
                + "function run()\n{\n    globallogic_utils::get_time_remaining();\n}\n"),
            ResolutionContext.RawContext, false, @"scripts\zm\gametypes\_globallogic_spawn.gsc");

        SymbolKey key = new("globallogic_utils", "get_time_remaining", SymbolKind.Function);
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> all =
            DatabaseQueries.FindReferences(database.Gsc, "raw", key);

        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> zm = DatabaseQueries.ScopeToIncludeGraph(
            all, @"scripts\zm\gametypes\_globallogic_utils.gsc", Bo3);
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> mp = DatabaseQueries.ScopeToIncludeGraph(
            all, @"scripts\mp\gametypes\_globallogic_utils.gsc", Bo3);

        // ZM keeps its own definition plus the call; MP keeps only its own definition.
        Assert.Equal(2, zm.Length);
        Assert.Single(mp);
        Assert.All(mp, reference => Assert.Equal(ReferenceKind.Definition, reference.Entry.Kind));
    }

    [Fact]
    public void ReferenceScopingDoesNotClaimASameNamedFunctionInAnotherNamespace()
    {
        // The trap in matching on NAME alone: spawn.gsc declares its own get_time_remaining in a
        // DIFFERENT namespace. That must not make it the declaring file for globallogic_utils'.
        ScriptDatabase database = new();
        TwoNamespaceCopies(database);

        database.Commit(
            AnalyzeBo3(
                @"c:\raw\spawn.gsc",
                "#using scripts\\zm\\gametypes\\_globallogic_utils;\n#namespace globallogic_spawn;\n"
                + "function get_time_remaining()\n{\n}\n"
                + "function run()\n{\n    globallogic_utils::get_time_remaining();\n}\n"),
            ResolutionContext.RawContext, false, @"scripts\zm\gametypes\_globallogic_spawn.gsc");

        SymbolKey key = new("globallogic_utils", "get_time_remaining", SymbolKind.Function);
        ImmutableArray<(ScriptRecord Record, ReferenceEntry Entry)> zm = DatabaseQueries.ScopeToIncludeGraph(
            DatabaseQueries.FindReferences(database.Gsc, "raw", key),
            @"scripts\zm\gametypes\_globallogic_utils.gsc",
            Bo3);

        // The qualified call still counts for the ZM copy despite spawn.gsc's own same-named function.
        Assert.Contains(zm, reference => reference.Entry.Kind == ReferenceKind.Call);
    }
}
