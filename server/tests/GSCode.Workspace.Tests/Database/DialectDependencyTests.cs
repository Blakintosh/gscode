using System.Collections.Immutable;
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
/// The Infinity Ward <c>#include</c> import gets the same dependency machinery as BO3's
/// <c>#using</c>: a <see cref="DependencyEdge"/> (so the include graph exists for navigation, rename
/// and merge scoping) and a <see cref="HitKind.DependencyPath"/> hit under the cursor (so ctrl-click
/// jumps to the target file). Both are exercised with an explicit CoD4 profile, since the lexer only
/// recognises <c>#include</c> on a merge dialect.
/// </summary>
public class DialectDependencyTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private const string Source = "#include common_scripts\\utility;\nrun()\n{\n\thelper();\n}\n";

    private static ParseResult Analyze()
    {
        return ScriptAnalysis.Analyze(
            @"c:\ws\scripts\maps\mp\_utility.gsc",
            ScriptLanguage.Gsc,
            SourceText.From(Source),
            NullInsertProvider.Instance,
            new NameTable(),
            Cod4);
    }

    [Fact]
    public void AnIncludeBecomesADependencyEdge()
    {
        ScriptRecord record = ScriptDatabase.BuildRecord(Analyze(), ResolutionContext.RawContext, isDirty: false);

        DependencyEdge edge = Assert.Single(record.Dependencies);
        Assert.Equal("common_scripts\\utility", edge.RawPath);
        Assert.False(edge.IsInsert);
    }

    [Fact]
    public void TheCursorOnAnIncludePathIsADependencyPath()
    {
        ParseResult result = Analyze();

        // Somewhere inside "common_scripts\utility" on line 0.
        Position onPath = new(0, Source.IndexOf("common_scripts", StringComparison.Ordinal) + 3);
        PositionHit hit = SymbolAtPosition.Resolve(result, onPath);

        Assert.Equal(HitKind.DependencyPath, hit.Kind);
    }

    [Fact]
    public void ANonPathPositionIsNotADependencyPath()
    {
        ParseResult result = Analyze();

        // On the #include keyword itself, not its path argument.
        PositionHit hit = SymbolAtPosition.Resolve(result, new Position(0, 2));

        Assert.NotEqual(HitKind.DependencyPath, hit.Kind);
    }
}
