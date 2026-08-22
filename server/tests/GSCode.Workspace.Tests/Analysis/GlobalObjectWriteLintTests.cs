using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Analysis;
using Xunit;

namespace GSCode.Workspace.Tests.Analysis;

/// <summary>
/// Writing to <c>level</c>, <c>self</c>, <c>game</c>, <c>anim</c> or <c>world</c> — the engine's own
/// objects. The names are the profile's, so the rule follows the dialect rather than a table here.
///
/// The distinction the rule lives or dies by is bare name versus write-THROUGH: <c>level.x = 1</c>
/// and <c>game[ "k" ] = 1</c> are how every script in every corpus uses these objects, and a rule
/// that looked at the base of a member or index expression would report all of them.
/// </summary>
public class GlobalObjectWriteLintTests
{
    private static ImmutableArray<Diagnostic> Lint(string body)
    {
        ParseResult result = ScriptAnalysis.Analyze(
            @"c:\ws\scripts\t.gsc",
            ScriptLanguage.Gsc,
            SourceText.From("function f()\n{\n" + body + "\n}\n"),
            NullInsertProvider.Instance,
            new NameTable());

        return GlobalObjectWriteLint.Analyze(result);
    }

    [Fact]
    public void AssigningToAGlobalIsAnError()
    {
        Diagnostic reported = Assert.Single(Lint("    anim = 1;"));

        Assert.Equal(GscDiagnosticCode.CannotAssignToGlobalObject, reported.Code);
        Assert.Equal(DiagnosticSeverity.Error, reported.Severity);
        Assert.Contains("anim", reported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryGlobalTheProfileNamesIsCovered()
    {
        Assert.Single(Lint("    level = 1;"));
        Assert.Single(Lint("    self = 1;"));
        Assert.Single(Lint("    game = 1;"));
        Assert.Single(Lint("    world = 1;"));
    }

    [Fact]
    public void CompoundAssignmentAndIncrementCountToo()
    {
        // `level += 1` is as impossible as `level = 1`; so is `level++`.
        Assert.Single(Lint("    level += 1;"));
        Assert.Single(Lint("    level++;"));
    }

    // --- What must stay silent ---

    [Fact]
    public void WritingAFieldOnAGlobalIsTheNormalWayToUseOne()
    {
        Assert.Empty(Lint("    level.things = [];"));
        Assert.Empty(Lint("    self.count = 1;"));
        Assert.Empty(Lint("    level.a.b = 1;"));
    }

    [Fact]
    public void WritingThroughAnIndexOnAGlobalIsAlsoNormal()
    {
        Assert.Empty(Lint("    game[ \"key\" ] = 1;"));
        Assert.Empty(Lint("    level.things[ 0 ] = 1;"));
    }

    [Fact]
    public void ReadingAGlobalIsNotWritingToIt()
    {
        Assert.Empty(Lint("    a = level;"));
        Assert.Empty(Lint("    level thread f();"));
        Assert.Empty(Lint("    level notify( \"x\" );"));
    }

    [Fact]
    public void AnOrdinaryLocalIsUntouched()
    {
        Assert.Empty(Lint("    levels = 1;"));
        Assert.Empty(Lint("    my_level = 1;"));
    }

    [Fact]
    public void WorldIsAnOrdinaryNameInADialectThatHasNone()
    {
        // The one portability trap here. Call of Duty 4 has no `world`, so a local called `world`
        // is a name like any other and reporting it would be a false Error on working code.
        Assert.True(GameProfile.Select("cod4"));
        try
        {
            Assert.Empty(Lint("    world = 1;"));

            // The globals every dialect does have are still reported under it.
            Assert.Single(Lint("    level = 1;"));
        }
        finally
        {
            GameProfile.Select("bo3");
        }
    }
}
