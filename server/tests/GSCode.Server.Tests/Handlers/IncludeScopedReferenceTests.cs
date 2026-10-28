using GSCode.Core;
using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// On an include dialect a function is keyed by the file that declares it, and a call reaches only
/// what its own file provides: that file's functions, its direct includes' own, or the one file a
/// path call names. Two questions that start somewhere other than the call have to keep to that.
///
/// A callback stored in a field is resolved where it was STORED. <c>level.cb = ::handler;</c> means
/// the handler that file reaches, whichever file later calls <c>[[ level.cb ]]()</c>.
///
/// Find-references asks from the cursor: on a path call it means the named file's function, not
/// the asking file's own one of the same name, and on a call nothing reaches it has no declaration
/// to gather references for, so it answers for this file alone rather than for every same-named
/// function in the workspace.
/// </summary>
public sealed class IncludeScopedReferenceTests
{
    private static readonly GameProfile s_cod4 = GameProfile.Cod4;

    private static readonly TestFile[] s_callbackFiles =
    [
        new TestFile(@"maps\_helpers_a.gsc", "handler( a_param )\n{\n}\n"),
        new TestFile(@"maps\_helpers_b.gsc", "handler( b_param )\n{\n}\n"),
        new TestFile(@"maps\_store_a.gsc", "#include maps\\_helpers_a;\n\ninit()\n{\n\tlevel.cb = ::handler;\n}\n"),
        new TestFile(@"maps\_store_b.gsc", "init()\n{\n\tlevel.cb2 = maps\\_helpers_b::handler;\n}\n"),
        new TestFile(@"maps\caller.gsc", "main()\n{\n\t[[ level.cb ]]();\n\t[[ level.cb2 ]]();\n}\n"),
    ];

    private static async Task<List<string>> ImplementationFilesAsync(int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_callbackFiles, s_cod4);
        workspace.Open(@"maps\caller.gsc");

        LocationOrLocationLinks? result = await new ImplementationHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new ImplementationParams { TextDocument = HandlerWorkspace.Identify(@"maps\caller.gsc"), Position = new Position(line, character) },
            CancellationToken.None);

        return result is null ? [] : [.. result.Select(static link => link.Location!.Uri.GetFileSystemPath().ToLowerInvariant())];
    }

    [Fact]
    public async Task ACallbackStoredBare_ResolvesInTheFileThatStoredIt()
    {
        // "\t[[ level.cb ]]();" — cb at column 10.
        List<string> files = await ImplementationFilesAsync(2, 10);

        Assert.Contains(files, static path => path.EndsWith(@"maps\_helpers_a.gsc", StringComparison.Ordinal));
        Assert.DoesNotContain(files, static path => path.EndsWith(@"maps\_helpers_b.gsc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallbackStoredByPath_ResolvesInTheFileItNames()
    {
        // "\t[[ level.cb2 ]]();" — cb2 at column 10.
        List<string> files = await ImplementationFilesAsync(3, 10);

        Assert.Contains(files, static path => path.EndsWith(@"maps\_helpers_b.gsc", StringComparison.Ordinal));
        Assert.DoesNotContain(files, static path => path.EndsWith(@"maps\_helpers_a.gsc", StringComparison.Ordinal));
    }

    private static readonly TestFile[] s_referenceFiles =
    [
        new TestFile(@"maps\a.gsc", "main()\n{\n\tmaps\\b::main();\n}\n"),
        new TestFile(@"maps\b.gsc", "main()\n{\n}\n"),
        new TestFile(@"maps\c.gsc", "run()\n{\n\tmaps\\b::main();\n}\n"),
        new TestFile(@"maps\_utility.gsc", "sp_only()\n{\n}\n"),
        new TestFile(@"maps\d.gsc", "run()\n{\n\tsp_only();\n}\n"),
        new TestFile(@"maps\e.gsc", "#include maps\\_utility;\n\nrun()\n{\n\tsp_only();\n}\n"),
    ];

    private static async Task<List<string>> ReferencesAsync(string openRelativePath, int line, int character)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(s_referenceFiles, s_cod4);
        workspace.Open(openRelativePath);

        LocationContainer? result = await new ReferencesHandler(workspace.Navigation, HandlerWorkspace.Selector).Handle(
            new ReferenceParams
            {
                TextDocument = HandlerWorkspace.Identify(openRelativePath),
                Position = new Position(line, character),
                Context = new ReferenceContext { IncludeDeclaration = true },
            },
            CancellationToken.None);

        return result is null
            ? []
            : [.. result.Select(static location => $"{Path.GetFileName(location.Uri.GetFileSystemPath()).ToLowerInvariant()}:{location.Range.Start.Line}")];
    }

    [Fact]
    public async Task ReferencesOnAPathCall_AreTheNamedFilesFunctions()
    {
        // In a.gsc, which declares a main of its own: "\tmaps\b::main();" — main at column 9.
        List<string> references = await ReferencesAsync(@"maps\a.gsc", 2, 9);

        Assert.Contains("b.gsc:0", references);
        Assert.Contains("a.gsc:2", references);
        Assert.Contains("c.gsc:2", references);
        Assert.DoesNotContain("a.gsc:0", references);
    }

    [Fact]
    public async Task ReferencesOnACallNothingReaches_StayInThisFile()
    {
        // d.gsc includes nothing, so its sp_only() reaches no declaration; e.gsc's, through its
        // include, is a different call to a function d cannot see.
        List<string> references = await ReferencesAsync(@"maps\d.gsc", 2, 1);

        Assert.Equal(["d.gsc:2"], references);
    }
}
