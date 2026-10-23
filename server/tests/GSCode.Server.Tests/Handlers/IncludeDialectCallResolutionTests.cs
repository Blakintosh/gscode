using GSCode.Core;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// A bare call on an include dialect means the function the calling file can reach: its own, then
/// one an <c>#include</c> or a path call brings in. Under <c>#include</c> every same-named function
/// shares one key — CoD4's animscripts hold 1,230 <c>main</c>s — so asking the key alone answers with
/// every one of them.
///
/// Hover took the first of those, which was whichever file the index listed first, and the
/// outgoing call hierarchy offered all of them. Both also paid for building the whole list: at
/// 50,000 heavy cod4 files a name is declared by about 2,000 copies, and hover on a call to the
/// file's own function took 40 ms, outgoing calls 104 ms. Driven through the handlers, because the
/// answer they show is the point.
/// </summary>
public sealed class IncludeDialectCallResolutionTests
{
    private static readonly GameProfile s_cod4 = GameProfile.Cod4;

    private const int Decoys = 8;

    // "helper( 1 );" is line 6, column 1; main is declared on line 4.
    private const string OwnSource = "helper( zeta )\n{\n}\n\nmain()\n{\n\thelper( 1 );\n}\n";

    private const string LibSource = "libfn( fromlib )\n{\n}\n";

    // "libfn( 1 );" is line 4, column 1.
    private const string UserSource = "#include maps\\lib;\n\nmain()\n{\n\tlibfn( 1 );\n}\n";

    /// <summary>The workspace: the asking files, and decoys declaring the same names elsewhere.</summary>
    private static async Task<HandlerWorkspace> BuildAsync()
    {
        List<TestFile> files =
        [
            new TestFile(@"maps\zz_own.gsc", OwnSource),
            new TestFile(@"maps\lib.gsc", LibSource),
            new TestFile(@"maps\user.gsc", UserSource),
        ];
        for ( int index = 0; index < Decoys; index++ )
        {
            files.Add(new TestFile($@"maps\decoy{index}.gsc", $"helper( decoy{index} )\n{{\n}}\n\nlibfn( decoy{index} )\n{{\n}}\n"));
        }

        return await HandlerWorkspace.BuildAsync(files, s_cod4);
    }

    private static async Task<string?> HoverAtAsync(string relativePath, int line, int character)
    {
        using HandlerWorkspace workspace = await BuildAsync();
        workspace.Open(relativePath);

        HoverHandler handler = new(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);
        Hover? hover = await handler.Handle(
            new HoverParams { TextDocument = HandlerWorkspace.Identify(relativePath), Position = new Position(line, character) },
            CancellationToken.None);
        return hover?.Contents.MarkupContent?.Value;
    }

    [Fact]
    public async Task HoverOnACallToTheFilesOwnFunctionShowsThatFunction()
    {
        string? markdown = await HoverAtAsync(@"maps\zz_own.gsc", 6, 1);

        Assert.NotNull(markdown);
        Assert.Contains("zeta", markdown!, StringComparison.Ordinal);
        Assert.DoesNotContain("decoy", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverOnACallToAnIncludedFunctionShowsTheIncludedOne()
    {
        string? markdown = await HoverAtAsync(@"maps\user.gsc", 4, 1);

        Assert.NotNull(markdown);
        Assert.Contains("fromlib", markdown!, StringComparison.Ordinal);
        Assert.DoesNotContain("decoy", markdown!, StringComparison.Ordinal);
    }

    // A file declaring main that path-calls another file's main — combat.gsc and cover_prone::main in
    // the shipped animscripts. "maps\pathed::main();" is line 3, column 13.
    private const string PathCallerSource = "main()\n{\n\tif ( 1 )\n\t\tmaps\\pathed::main();\n}\n";

    private const string PathedSource = "main( pathedparam )\n{\n}\n";

    [Fact]
    public async Task HoverOnAPathCallShowsTheNamedFilesFunctionNotTheCallersOwn()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"maps\pathcaller.gsc", PathCallerSource), new TestFile(@"maps\pathed.gsc", PathedSource)],
            s_cod4);
        workspace.Open(@"maps\pathcaller.gsc");
        HoverHandler handler = new(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        Hover? hover = await handler.Handle(
            new HoverParams { TextDocument = HandlerWorkspace.Identify(@"maps\pathcaller.gsc"), Position = new Position(3, 15) },
            CancellationToken.None);

        string? markdown = hover?.Contents.MarkupContent?.Value;
        Assert.NotNull(markdown);
        Assert.Contains("pathedparam", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutgoingCallsFollowAPathCallToTheNamedFile()
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [new TestFile(@"maps\pathcaller.gsc", PathCallerSource), new TestFile(@"maps\pathed.gsc", PathedSource)],
            s_cod4);
        workspace.Open(@"maps\pathcaller.gsc");
        CallHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        Container<CallHierarchyItem>? prepared = await handler.Handle(
            new CallHierarchyPrepareParams { TextDocument = HandlerWorkspace.Identify(@"maps\pathcaller.gsc"), Position = new Position(0, 0) },
            CancellationToken.None);
        Container<CallHierarchyOutgoingCall>? outgoing = await handler.Handle(
            new CallHierarchyOutgoingCallsParams { Item = Assert.Single(prepared!) }, CancellationToken.None);

        CallHierarchyOutgoingCall call = Assert.Single(outgoing!);
        Assert.EndsWith("pathed.gsc", call.To.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HoverOnACallReachedThroughANestedIncludeShowsTheIncludedOne()
    {
        // The compiler flattens an include chain: maps\_createpath includes maps\_utility and calls
        // flag_init, which lives in common_scripts\utility, which maps\_utility includes.
        // "deepfn( 1 );" is line 4, column 1.
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
            [
                new TestFile(@"maps\top.gsc", "#include maps\\mid;\n\nmain()\n{\n\tdeepfn( 1 );\n}\n"),
                new TestFile(@"maps\mid.gsc", "#include maps\\deep;\n\nmidfn()\n{\n}\n"),
                new TestFile(@"maps\deep.gsc", "deepfn( deepparam )\n{\n}\n"),
                new TestFile(@"maps\adecoy.gsc", "deepfn( decoyparam )\n{\n}\n"),
            ],
            s_cod4);
        workspace.Open(@"maps\top.gsc");
        HoverHandler handler = new(workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

        Hover? hover = await handler.Handle(
            new HoverParams { TextDocument = HandlerWorkspace.Identify(@"maps\top.gsc"), Position = new Position(4, 1) },
            CancellationToken.None);

        string? markdown = hover?.Contents.MarkupContent?.Value;
        Assert.NotNull(markdown);
        Assert.Contains("deepparam", markdown!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutgoingCallsNameOnlyTheFunctionTheCallReaches()
    {
        using HandlerWorkspace workspace = await BuildAsync();
        workspace.Open(@"maps\zz_own.gsc");
        CallHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);

        Container<CallHierarchyItem>? prepared = await handler.Handle(
            new CallHierarchyPrepareParams { TextDocument = HandlerWorkspace.Identify(@"maps\zz_own.gsc"), Position = new Position(4, 0) },
            CancellationToken.None);
        CallHierarchyItem main = Assert.Single(prepared!);
        Container<CallHierarchyOutgoingCall>? outgoing = await handler.Handle(
            new CallHierarchyOutgoingCallsParams { Item = main }, CancellationToken.None);

        CallHierarchyOutgoingCall call = Assert.Single(outgoing!);
        Assert.Equal("helper", call.To.Name);
        Assert.EndsWith("zz_own.gsc", call.To.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
    }
}
