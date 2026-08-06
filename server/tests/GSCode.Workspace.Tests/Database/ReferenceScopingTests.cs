using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Reference scoping. Under <c>#include</c> a function carries no namespace, so every same-named
/// function in the workspace shares one key — CoD4's animscripts hold 1,230 <c>main()</c>s.
/// Narrowing has to keep exactly the files that can REACH the declaring one, by all three routes:
/// being it, importing it, or path-calling it.
///
/// The path-call route is the one worth testing hardest. A first attempt checked imports only, and
/// a function whose callers all reach it by path went from 1,230 references to zero — a wrong-small
/// answer that reads as "this function is dead", which is worse than the noise it replaced.
///
/// BO3 needs the same narrowing for a smaller reason, covered at the end: the namespace is in the
/// key but does not pin a FILE, since the MP and ZM copies of a script declare the same
/// <c>#namespace</c>. It used to be exempted here on the theory that the key already settled it.
/// </summary>
public class ReferenceScopingTests
{
    private static readonly GameProfile Cod4 = GameProfile.ByName("cod4")!;

    /// <summary>The range every synthetic path call and reference shares, so they attribute to it.</summary>
    private static readonly TextRange PathCallRange = new(new Position(1, 1), new Position(1, 5));

    private static ScriptRecord Record(
        string relativePath, ImmutableArray<string> includes = default, ImmutableArray<string> pathCalls = default,
        ImmutableArray<FunctionSymbol> functions = default)
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
            Functions = functions.IsDefault ? [] : functions,
            PathCallTargets = pathCalls.IsDefault
                ? []
                : [.. pathCalls.Select(static path => new PathCallReference(path, PathCallRange))],
        };
    }

    private static ImmutableArray<(ScriptRecord, ReferenceEntry)> Refs(params ScriptRecord[] records)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder builder =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( ScriptRecord record in records )
        {
            builder.Add((record, new ReferenceEntry(
                new SymbolKey(null, "main", SymbolKind.Function), PathCallRange, ReferenceKind.Call)));
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
    public void AFilesOwnDeclarationIsNotAReferenceToAnotherFilesFunction()
    {
        // The reported symptom: find-references on combat's main() listed cover_prone's own main()
        // and _mgturret's own main(), because both files path-call combat and so were kept whole.
        // A bare name in a file that declares it means THAT file's function.
        ImmutableArray<FunctionSymbol> declaresMain =
        [
            new FunctionSymbol { Name = "main", KeyName = "main", Namespace = "", NameRange = PathCallRange, FullRange = PathCallRange },
        ];

        ScriptRecord coverProne = Record(
            @"animscripts\cover_prone.gsc",
            pathCalls: [@"animscripts\combat"],
            functions: declaresMain);

        // Its OWN main(), at a range that is not one of its path-call sites.
        ImmutableArray<(ScriptRecord, ReferenceEntry)> ownDeclaration =
        [
            (coverProne, new ReferenceEntry(
                new SymbolKey(null, "main", SymbolKind.Function),
                new TextRange(new Position(99, 1), new Position(99, 5)),
                ReferenceKind.Definition)),
        ];

        Assert.Empty(DatabaseQueries.ScopeToIncludeGraph(ownDeclaration, @"animscripts\combat.gsc", Cod4));

        // But its combat::main() call, which IS at a path-call site, still counts.
        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Refs(coverProne), @"animscripts\combat.gsc", Cod4));
    }

    [Fact]
    public void APathCallToADifferentFileIsNotAReference()
    {
        // corner.gsc calls both combat::main() and cover_behavior::main(). Only the first belongs to
        // combat, and the path at each site is what says so.
        ScriptRecord corner = Record(
            @"animscripts\corner.gsc",
            pathCalls: [@"animscripts\cover_behavior"]);

        Assert.Empty(DatabaseQueries.ScopeToIncludeGraph(Refs(corner), @"animscripts\combat.gsc", Cod4));
    }

    /// <summary>
    /// A BO3 reference: the key carries a namespace, which is what makes it a namespace-dialect key
    /// at all. <see cref="Refs"/> builds the merge-dialect form (namespace null) and the two must
    /// not be mixed — a null-namespace key handed to BO3 describes nothing the dialect can produce.
    /// </summary>
    private static ImmutableArray<(ScriptRecord, ReferenceEntry)> Bo3Refs(params ScriptRecord[] records)
    {
        ImmutableArray<(ScriptRecord, ReferenceEntry)>.Builder builder =
            ImmutableArray.CreateBuilder<(ScriptRecord, ReferenceEntry)>();

        foreach ( ScriptRecord record in records )
        {
            builder.Add((record, new ReferenceEntry(
                new SymbolKey("globallogic_utils", "get_time_remaining", SymbolKind.Function),
                PathCallRange,
                ReferenceKind.Call)));
        }

        return builder.ToImmutable();
    }

    [Fact]
    public void BlackOps3NarrowsByUsing_BecauseANamespaceDoesNotPinAFile()
    {
        // The namespace is in the key and still does not settle it: the MP and ZM copies of
        // `_globallogic_utils.gsc` both declare `#namespace globallogic_utils`. What separates them
        // is the `#using` graph, so a caller that imports the ZM copy counts only against ZM.
        const string zm = @"scripts\zm\gametypes\_globallogic_utils";
        const string mp = @"scripts\mp\gametypes\_globallogic_utils";

        ScriptRecord zmCaller = Record(@"scripts\zm\gametypes\_globallogic_spawn", includes: [zm]);

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Bo3Refs(zmCaller), zm, GameProfile.BlackOps3));
        Assert.Empty(DatabaseQueries.ScopeToIncludeGraph(Bo3Refs(zmCaller), mp, GameProfile.BlackOps3));
    }

    [Fact]
    public void BlackOps3KeepsADeclaringFilesOwnReference()
    {
        // The file IS the declaring one, so its own reference counts with no import at all.
        const string zm = @"scripts\zm\gametypes\_globallogic_utils";

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Bo3Refs(Record(zm)), zm, GameProfile.BlackOps3));
    }

    [Fact]
    public void BlackOps3DoesNotClaimASameNamedFunctionInAnotherNamespace()
    {
        // The trap in matching on NAME alone. This file declares its own get_time_remaining under a
        // different namespace AND imports the ZM copy. The "declares it itself, so the reference is
        // its own" shortcut must not fire, or the ZM copy loses a caller it really has.
        const string zm = @"scripts\zm\gametypes\_globallogic_utils";

        ScriptRecord caller = Record(
            @"scripts\zm\gametypes\_globallogic_spawn",
            includes: [zm],
            functions:
            [
                new FunctionSymbol
                {
                    Name = "get_time_remaining",
                    KeyName = "get_time_remaining",
                    Namespace = "globallogic_spawn",
                    NameRange = PathCallRange,
                    FullRange = PathCallRange,
                },
            ]);

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Bo3Refs(caller), zm, GameProfile.BlackOps3));
    }

    [Fact]
    public void AnUnknownDeclaringFileNarrowsNothing()
    {
        // Ambiguity must stay wide: a confidently wrong narrow answer is not recoverable.
        ScriptRecord stranger = Record(@"animscripts\walk");

        Assert.Single(DatabaseQueries.ScopeToIncludeGraph(Refs(stranger), "", Cod4));
    }
}
