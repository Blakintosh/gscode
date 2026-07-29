using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Reference scoping on the merge dialects. Under <c>#include</c> a function carries no namespace,
/// so every same-named function in the workspace shares one key — CoD4's animscripts hold 1,230
/// <c>main()</c>s. Narrowing has to keep exactly the files that can REACH the declaring one, by all
/// three routes: being it, importing it, or path-calling it.
///
/// The path-call route is the one worth testing hardest. A first attempt checked imports only, and
/// a function whose callers all reach it by path went from 1,230 references to zero — a wrong-small
/// answer that reads as "this function is dead", which is worse than the noise it replaced.
/// </summary>
public class ReferenceScopingTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    private static ScriptRecord Record(
        string relativePath, ImmutableArray<string> includes = default, ImmutableArray<string> pathCalls = default)
    {
        ImmutableArray<DependencyEdge>.Builder edges = ImmutableArray.CreateBuilder<DependencyEdge>();
        foreach ( string include in includes.IsDefault ? [] : includes )
        {
            edges.Add(new DependencyEdge(include, "", false, new TextRange()));
        }

        return new ScriptRecord
        {
            Path = @"C:\raw\" + relativePath + ".gsc",
            ContextId = "raw",
            ContentHash = 0,
            Language = ScriptLanguage.Gsc,
            RelativePath = relativePath,
            Dependencies = edges.ToImmutable(),
            PathCallTargets = pathCalls.IsDefault ? [] : pathCalls,
        };
    }

    private static ImmutableArray<(ScriptRecord, ReferenceEntry)> Refs(params ScriptRecord[] records)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder builder =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( ScriptRecord record in records )
        {
            builder.Add((record, new ReferenceEntry(
                new SymbolKey(null, "main", SymbolKind.Function), new TextRange(), ReferenceKind.Call)));
        }

        return builder.ToImmutable();
    }

    [Fact]
    public void APathCallReachesTheDeclaringFile_WithoutAnyImport()
    {
        // The case that matters: corner.gsc calls animscripts\combat::main() and does NOT include it.
        ScriptRecord caller = Record(@"animscripts\corner", pathCalls: [@"animscripts\combat"]);

        ImmutableArray<(ScriptRecord, ReferenceEntry)> kept =
            DatabaseQueries.ScopeToIncludeGraph(Refs(caller), @"animscripts\combat", Cod4);

        Assert.Single(kept);
    }

    [Fact]
    public void AnImportReachesIt_AndAnUnrelatedFileDoesNot()
    {
        ScriptRecord importer = Record(@"animscripts\melee", includes: [@"animscripts\combat"]);
        ScriptRecord stranger = Record(@"animscripts\walk");

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Refs(importer), @"animscripts\combat", Cod4));
        Assert.Empty(DatabaseQueries.ScopeToIncludeGraph(Refs(stranger), @"animscripts\combat", Cod4));
    }

    [Fact]
    public void TheDeclaringFileItselfCounts()
    {
        ScriptRecord self = Record(@"animscripts\combat");

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Refs(self), @"animscripts\combat", Cod4));
    }

    [Fact]
    public void UnrelatedSameNamedFunctionsAreDropped()
    {
        // The reported symptom: every animscript declares main(), and all of them shared one key.
        ScriptRecord reacher = Record(@"animscripts\corner", pathCalls: [@"animscripts\combat"]);
        ScriptRecord[] strangers =
        [
            Record(@"animscripts\walk"), Record(@"animscripts\prone"), Record(@"animscripts\stand"),
        ];

        ImmutableArray<(ScriptRecord, ReferenceEntry)> kept = DatabaseQueries.ScopeToIncludeGraph(
            Refs([reacher, .. strangers]), @"animscripts\combat", Cod4);

        Assert.Single(kept);
    }

    [Fact]
    public void ReachingAnotherDeclarationDoesNotDropTheReacher()
    {
        // combat.gsc path-calls cover_prone and _mgturret, and all three declare main(). The lens on
        // combat.gsc's own main must still narrow to combat.gsc — the reachable strangers are other
        // functions entirely, not competing definitions of this one.
        ScriptRecord combat = Record(
            @"animscripts\combat.gsc",
            pathCalls: [@"animscripts\cover_prone", @"maps\_mgturret"]);
        ScriptRecord stranger = Record(@"animscripts\cover_prone.gsc");

        ImmutableArray<(ScriptRecord, ReferenceEntry)> kept = DatabaseQueries.ScopeToIncludeGraph(
            Refs(combat, stranger), @"animscripts\combat.gsc", Cod4);

        Assert.Single(kept);
    }

    [Fact]
    public void BlackOps3IsUntouched()
    {
        // #using puts the namespace in the key, so the ambiguity never arises and nothing is dropped.
        ScriptRecord stranger = Record(@"scripts\shared\unrelated");

        ImmutableArray<(ScriptRecord, ReferenceEntry)> kept =
            DatabaseQueries.ScopeToIncludeGraph(Refs(stranger), @"scripts\shared\other", GameProfile.BlackOps3);

        Assert.Single(kept);
    }

    [Fact]
    public void AnUnknownDeclaringFileNarrowsNothing()
    {
        // Ambiguity must stay wide: a confidently wrong narrow answer is not recoverable.
        ScriptRecord stranger = Record(@"animscripts\walk");

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Refs(stranger), "", Cod4));
    }
}
