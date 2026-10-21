using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// CodeLens keeps the part of each count that comes from other files while only the open file is
/// written (<see cref="LensCountCache"/>). A kept count is only worth having if it is never stale,
/// so each fact here writes something between two requests and requires the second count to see
/// it: another file's new call, a file that did not exist, and the open file's own edit — and the
/// same for a raw script a mod carries its own copy of, which is where the counts cost most.
/// </summary>
public sealed class CodeLensCountCacheTests
{
    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    private const string CallerSource = "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    lib::helper();\n}\n";

    /// <summary>The count on <c>helper</c>'s lens, from one handler whose cache lives across requests.</summary>
    private static async Task<int> HelperCountAsync(HandlerWorkspace workspace, CodeLensHandler handler)
    {
        CodeLensContainer? lenses = await handler.Handle(
            new CodeLensParams { TextDocument = HandlerWorkspace.Identify(@"scripts\lib.gsc") }, CancellationToken.None);

        Assert.NotNull(lenses);
        CodeLens lens = Assert.Single(lenses);
        string title = lens.Command!.Title;
        return int.Parse(title.Split(' ')[0]);
    }

    /// <summary>Writes a file's new text into the index the way an editor's change does.</summary>
    private static void Commit(HandlerWorkspace workspace, string relativePath, string text)
    {
        OpenDocument document = workspace.Open(relativePath, text);
        ParseResult result = workspace.Documents.AnalyzeIfStale(document);
        workspace.Database.Commit(
            result, workspace.Resolver.GetContext(TestPaths.Raw(relativePath)), isDirty: true, relativePath);
    }

    private static async Task<(HandlerWorkspace Workspace, CodeLensHandler Handler)> BuildAsync()
    {
        HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\lib.gsc", LibSource),
            new TestFile(@"scripts\caller.gsc", CallerSource),
        ]);
        workspace.Open(@"scripts\lib.gsc");

        CodeLensHandler handler = new(
            workspace.Navigation, new ServerSettings { CodeLensEnabled = true }, HandlerWorkspace.Selector, new LensCountCache());
        return (workspace, handler);
    }

    [Fact]
    public async Task AnotherFilesNewCallIsCounted()
    {
        (HandlerWorkspace workspace, CodeLensHandler handler) = await BuildAsync();
        using HandlerWorkspace disposing = workspace;

        Assert.Equal(1, await HelperCountAsync(workspace, handler));

        Commit(workspace, @"scripts\caller.gsc", CallerSource.Replace("    lib::helper();\n", "    lib::helper();\n    lib::helper();\n"));

        Assert.Equal(2, await HelperCountAsync(workspace, handler));
    }

    [Fact]
    public async Task ANewFilesCallIsCounted()
    {
        (HandlerWorkspace workspace, CodeLensHandler handler) = await BuildAsync();
        using HandlerWorkspace disposing = workspace;

        Assert.Equal(1, await HelperCountAsync(workspace, handler));

        Commit(workspace, @"scripts\second.gsc", CallerSource.Replace("#namespace caller;", "#namespace second;"));

        Assert.Equal(2, await HelperCountAsync(workspace, handler));
    }

    [Fact]
    public async Task ARawScriptAModCopies_StillSeesAnotherFilesNewCall()
    {
        // A mod's copy of lib.gsc sits beside the raw one. The raw file's counts are kept anyway —
        // nothing the raw scripts can see depends on the mod — so they have to stay right.
        (HandlerWorkspace workspace, CodeLensHandler handler) = await BuildAsync();
        using HandlerWorkspace disposing = workspace;

        string modCopy = TestPaths.ModsRoot + @"\m\scripts\lib.gsc";
        workspace.Files.AddFile(modCopy, LibSource);
        OpenDocument copy = workspace.Documents.Open(modCopy, LibSource, version: 1);
        workspace.Database.Commit(
            workspace.Documents.AnalyzeIfStale(copy), workspace.Resolver.GetContext(modCopy), isDirty: false, @"scripts\lib.gsc");

        Assert.Equal(1, await HelperCountAsync(workspace, handler));

        Commit(workspace, @"scripts\caller.gsc", CallerSource.Replace("    lib::helper();\n", "    lib::helper();\n    lib::helper();\n"));

        Assert.Equal(2, await HelperCountAsync(workspace, handler));
    }

    [Fact]
    public async Task TheOpenFilesOwnEditIsCounted()
    {
        // The open file's writes are the ones the kept counts survive, so its own part is counted
        // again every time. A call added inside a body leaves what the file declares and imports
        // alone, so the kept counts stay; the new call must still show.
        (HandlerWorkspace workspace, CodeLensHandler handler) = await BuildAsync();
        using HandlerWorkspace disposing = workspace;

        Assert.Equal(1, await HelperCountAsync(workspace, handler));

        Commit(
            workspace,
            @"scripts\lib.gsc",
            "#namespace lib;\nfunction helper()\n{\n    helper();\n}\n");

        Assert.Equal(2, await HelperCountAsync(workspace, handler));
    }
}
