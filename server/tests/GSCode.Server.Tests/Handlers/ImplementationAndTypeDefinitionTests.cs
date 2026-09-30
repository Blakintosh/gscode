using GSCode.Server.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The two navigation requests that answer what go-to-definition cannot: which subclasses override
/// a method, and what a local actually holds.
///
/// Driven through the handlers over a real two-file workspace, because both answers come
/// from the indexed class graph rather than from the open file's own syntax — an override lives in
/// another class, and often another file, which is the whole reason the request exists.
///
/// The negative cases are here for the same reason the positive ones are. Both handlers decline
/// rather than falling back to the declaration, and a handler that silently answers a different
/// question is indistinguishable from one that works until someone checks.
/// </summary>
public sealed class ImplementationAndTypeDefinitionTests
{
    // class cScene declares play() on line 3; cAwarenessScene overrides it on line 9. Both names
    // start at character 13, and the class name of cScene at character 6 of line 1.
    private const string SceneSource =
        "#namespace scene;\n"
        + "class cScene\n"
        + "{\n"
        + "    function play()\n"
        + "    {\n"
        + "    }\n"
        + "}\n"
        + "class cAwarenessScene : cScene\n"
        + "{\n"
        + "    function play()\n"
        + "    {\n"
        + "    }\n"
        + "}\n";

    private const string MainSource =
        "#using scripts\\scene;\n"
        + "#namespace game;\n"
        + "function run()\n"
        + "{\n"
        + "    s = new cScene();\n"
        + "    s->play();\n"
        + "    t = \"text\";\n"
        + "    x = t;\n"
        + "}\n";

    /// <summary>
    /// One indexed workspace, handed to whichever handler the test wants. The two files are indexed
    /// for real: an override the store has not seen is an override the class graph cannot report.
    /// </summary>
    private static async Task<T> WithWorkspaceAsync<T>(
        Func<HandlerWorkspace, TextDocumentIdentifier, Task<T>> body, string openFile)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\scene.gsc", SceneSource),
            new TestFile(@"scripts\main.gsc", MainSource),
        ]);

        string relativePath = @"scripts\" + openFile;
        workspace.Open(relativePath);
        return await body(workspace, HandlerWorkspace.Identify(relativePath));
    }

    private static Task<LocationOrLocationLinks?> ImplementationAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                ImplementationHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
                ImplementationParams request = new()
                {
                    TextDocument = document,
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            },
            openFile);
    }

    private static Task<LocationOrLocationLinks?> TypeDefinitionAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                TypeDefinitionHandler handler = new(
                    workspace.Navigation, workspace.Builtins, workspace.ObjectFields, HandlerWorkspace.Selector);

                TypeDefinitionParams request = new()
                {
                    TextDocument = document,
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            },
            openFile);
    }

    private static Location SingleLocation(LocationOrLocationLinks? result)
    {
        Assert.NotNull(result);
        LocationOrLocationLink single = Assert.Single(result!);
        Assert.NotNull(single.Location);
        return single.Location!;
    }

    [Fact]
    public async Task AMethodDeclarationFindsTheSubclassThatOverridesIt()
    {
        LocationOrLocationLinks? result = await ImplementationAtAsync("scene.gsc", 3, 14);

        Location location = SingleLocation(result);
        Assert.Equal(9, location.Range.Start.Line);
        Assert.Equal(13, location.Range.Start.Character);
    }

    [Fact]
    public async Task ATopLevelFunctionHasNoImplementations()
    {
        // `run` is not a method, so there is nothing to override — and the handler must say so
        // rather than handing back its own declaration, which go-to-definition already gives.
        LocationOrLocationLinks? result = await ImplementationAtAsync("main.gsc", 2, 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task ALocalHoldingAnInstanceFindsItsClass()
    {
        LocationOrLocationLinks? result = await TypeDefinitionAtAsync("main.gsc", 5, 4);

        Location location = SingleLocation(result);
        Assert.Contains("scene.gsc", location.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, location.Range.Start.Line);
        Assert.Equal(6, location.Range.Start.Character);
    }

    [Fact]
    public async Task ALocalHoldingAStringHasNoTypeDefinition()
    {
        // A string is a type, not an identity: there is no declaration anywhere to jump to.
        LocationOrLocationLinks? result = await TypeDefinitionAtAsync("main.gsc", 7, 8);

        Assert.Null(result);
    }
}