using System.Collections.Immutable;
using System.Linq;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
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
    public void TheAskingFileItselfIsAlwaysInScope()
    {
        // A helper defined in the asking file wins even with no #include of it.
        Assert.True(DatabaseQueries.IsInIncludeScope(
            @"common_scripts\utility.gsc", @"common_scripts\utility.gsc", includedPaths: []));
    }
}
