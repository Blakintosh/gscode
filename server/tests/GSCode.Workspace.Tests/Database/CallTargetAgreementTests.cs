using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using Xunit;

namespace GSCode.Workspace.Tests.Database;

/// <summary>
/// Hover, signature help and parameter hints describe ONE declaration of the function a call
/// names, and it has to be the one go-to-definition lands on: the declaration the calling file
/// reaches. Two shapes broke that.
///
/// On an include dialect a path call names a FILE: <c>maps\mp\_utility::foo()</c> is not
/// <c>maps\_utility::foo()</c>, though both files' stem is <c>_utility</c>, and
/// <c>maps\_utility::array_thread()</c> reaches common_scripts\utility through maps\_utility's
/// include. On a namespace dialect a namespace does not pin a file: an mp and a zm copy both declare
/// <c>#namespace util</c>, and an mp script that <c>#using</c>s the mp copy means that one.
///
/// Several unreached copies, so an index order that happens to list the reached one first cannot
/// pass a fact by accident.
/// </summary>
public sealed class CallTargetAgreementTests
{
    private static string ApiDirectory => Path.Combine(AppContext.BaseDirectory, "Api");

    private static readonly GameProfile s_cod4 = GameProfile.Cod4;

    private const string Cod4Caller =
        "#include maps\\_utility;\n"                                   // 0
        + "\n"                                                         // 1
        + "main()\n"                                                   // 2
        + "{\n"                                                        // 3
        + "    maps\\mp\\_utility::foo( 1 );\n"                        // 4  "foo( " ends at 28
        + "    maps\\_utility::array_thread( 1, 2 );\n"                // 5  "array_thread( " ends at 34
        + "    array_thread( 1, 2 );\n"                                // 6  "array_thread( " ends at 18
        + "}\n";

    private static TestWorkspace Cod4Workspace()
    {
        return TestWorkspace.Build(
            [
                new TestFile(@"maps\_utility.gsc", "#include common_scripts\\utility;\n\nfoo( a, b, c )\n{\n}\n"),
                new TestFile(@"maps\mp\_utility.gsc", "foo( x )\n{\n}\n"),
                new TestFile(@"maps\_zombie\_utility.gsc", "foo( z1, z2 )\n{\n}\n"),
                new TestFile(@"common_scripts\utility.gsc", "array_thread( ents, func )\n{\n}\n"),
                new TestFile(@"maps\other\array_thread_copy.gsc", "array_thread( other )\n{\n}\n"),
                new TestFile(@"maps\caller.gsc", Cod4Caller),
            ],
            s_cod4);
    }

    private const string Bo3Caller =
        "#using scripts\\mp\\gametypes\\_util;\n"                      // 0
        + "\n"                                                         // 1
        + "#namespace util;\n"                                         // 2
        + "\n"                                                         // 3
        + "function main()\n"                                          // 4
        + "{\n"                                                        // 5
        + "    util::f( 1 );\n"                                        // 6  "f( " ends at 12
        + "    f( 1 );\n"                                              // 7  "f( " ends at 7
        + "}\n";

    private static TestWorkspace Bo3Workspace()
    {
        return TestWorkspace.Build(
        [
            new TestFile(@"scripts\zm\gametypes\_util.gsc", "#namespace util;\nfunction f( zm_a, zm_b )\n{\n}\n"),
            new TestFile(@"scripts\cp\gametypes\_util.gsc", "#namespace util;\nfunction f( cp_a, cp_b, cp_c )\n{\n}\n"),
            new TestFile(@"scripts\mp\gametypes\_util.gsc", "#namespace util;\nfunction f( mp_a )\n{\n}\n"),
            new TestFile(@"scripts\mp\caller.gsc", Bo3Caller),
        ]);
    }

    private static string? FirstParameter(SignatureEngine engine, ParseResult result, Position at)
    {
        SignatureResult? signature = engine.Resolve(result, "raw", at);
        if ( signature is null || signature.Parameters.Length == 0 )
        {
            return null;
        }

        return signature.Parameters[0].Label;
    }

    [Fact]
    public void SignatureHelp_OnAPathCall_DescribesTheFileItNames()
    {
        using TestWorkspace workspace = Cod4Workspace();
        SignatureEngine engine = new(workspace.Database, BuiltinApiSet.Load(ApiDirectory));
        ParseResult result = workspace.Analyze(@"maps\caller.gsc");

        Assert.Equal("x", FirstParameter(engine, result, new Position(4, 28)));
    }

    [Fact]
    public void SignatureHelp_OnAPathCall_FollowsTheNamedFilesIncludes()
    {
        using TestWorkspace workspace = Cod4Workspace();
        SignatureEngine engine = new(workspace.Database, BuiltinApiSet.Load(ApiDirectory));
        ParseResult result = workspace.Analyze(@"maps\caller.gsc");

        Assert.Equal("ents", FirstParameter(engine, result, new Position(5, 34)));
    }

    [Fact]
    public void ABareCall_OnAnIncludeDialect_FollowsTheIncludeChain()
    {
        // Signature help and the parameter hints share this answer.
        using TestWorkspace workspace = Cod4Workspace();
        ParseResult result = workspace.Analyze(@"maps\caller.gsc");

        FunctionSymbol? found = CallResolution.UnqualifiedFunction(workspace.Database.Gsc, "raw", result, "array_thread", s_cod4);

        Assert.NotNull(found);
        Assert.Equal("ents", found!.Parameters[0].Name);
    }

    [Fact]
    public void APathCall_ForTheHints_FollowsTheNamedFilesIncludes()
    {
        using TestWorkspace workspace = Cod4Workspace();

        FunctionSymbol? found = CallResolution.PathQualifiedFunction(
            workspace.Database.Gsc, "raw", TestPaths.Raw(@"maps\caller.gsc"), @"maps\_utility", "array_thread");

        Assert.NotNull(found);
        Assert.Equal("ents", found!.Parameters[0].Name);
    }

    [Fact]
    public void SignatureHelp_OnANamespaceCall_DescribesTheCopyTheFileUses()
    {
        using TestWorkspace workspace = Bo3Workspace();
        SignatureEngine engine = new(workspace.Database, BuiltinApiSet.Load(ApiDirectory));
        ParseResult result = workspace.Analyze(@"scripts\mp\caller.gsc");

        Assert.Equal("mp_a", FirstParameter(engine, result, new Position(6, 12)));
        Assert.Equal("mp_a", FirstParameter(engine, result, new Position(7, 7)));
    }

    [Fact]
    public void ResolveCall_OnANamespaceCall_ListsTheCopyTheFileUsesFirst()
    {
        // Hover renders the first declaration ResolveCall returns.
        using TestWorkspace workspace = Bo3Workspace();
        ParseResult result = workspace.Analyze(@"scripts\mp\caller.gsc");

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            workspace.Database.Gsc, "raw", TestPaths.Raw(@"scripts\mp\caller.gsc"),
            new SymbolKey("util", "f", SymbolKind.Function), ReferenceKind.Call, result.Extraction.DeclaredNamespaces);

        Assert.Equal(3, resolved.Length);
        Assert.Equal("mp_a", resolved[0].Function.Parameters[0].Name);
    }

    [Fact]
    public void ResolveCall_OnAnIncludeDialect_FindsNothingTheFileCannotReach()
    {
        // Hover renders the first declaration ResolveCall returns, and there is none to render: the
        // engine finds a function only through the file's own declarations and its include chain.
        using TestWorkspace workspace = TestWorkspace.Build(
            [
                new TestFile(@"maps\_utility.gsc", "sp_only( sp_b )\n{\n}\n"),
                new TestFile(@"maps\mp\_utility.gsc", "mp_only( mp_b )\n{\n}\n"),
                new TestFile(@"maps\mp\caller.gsc", "#include maps\\mp\\_utility;\n\nmain()\n{\n    sp_only();\n}\n"),
            ],
            s_cod4);
        ParseResult result = workspace.Analyze(@"maps\mp\caller.gsc");

        ImmutableArray<ResolvedFunction> resolved = MethodResolution.ResolveCall(
            workspace.Database.Gsc, "raw", TestPaths.Raw(@"maps\mp\caller.gsc"),
            new SymbolKey(null, "sp_only", SymbolKind.Function), ReferenceKind.Call, result.Extraction.DeclaredNamespaces);

        Assert.Empty(resolved);
    }
}
