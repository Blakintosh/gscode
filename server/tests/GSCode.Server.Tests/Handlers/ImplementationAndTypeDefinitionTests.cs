using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The two navigation requests that answer what go-to-definition cannot: which subclasses override
/// a method, and what a local actually holds.
///
/// Driven through the handlers over a real two-file workspace on disk, because both answers come
/// from the indexed class graph rather than from the open file's own syntax — an override lives in
/// another class, and often another file, which is the whole reason the request exists.
///
/// The negative cases are here for the same reason the positive ones are. Both handlers decline
/// rather than falling back to the declaration, and a handler that silently answers a different
/// question is indistinguishable from one that works until someone checks.
/// </summary>
public sealed class ImplementationAndTypeDefinitionTests : IDisposable
{
    private readonly string _root;

    public ImplementationAndTypeDefinitionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-navigation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch ( IOException )
        {
            // Best-effort cleanup; a locked file left behind costs nothing a later run cannot fix.
        }
    }

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
    /// One indexed workspace, handed to whichever handler the test wants. The two files are written
    /// and indexed for real: an override the store has not seen is an override the class graph
    /// cannot report.
    /// </summary>
    private async Task<T> WithWorkspaceAsync<T>(Func<NavigationSupport, TextDocumentSelector, string, Task<T>> body, string openFile)
    {
        File.WriteAllText(Path.Combine(_root, @"scripts\scene.gsc"), SceneSource);
        File.WriteAllText(Path.Combine(_root, @"scripts\main.gsc"), MainSource);

        GameProfile previous = GameProfile.Active;
        GameProfile.Select("bo3");
        try
        {
            PhysicalFileSystem fileSystem = new();
            RootConfig config = RootConfig.Create(
                rawEnabled: true, rawPath: _root, modsPath: null, workspaceFolders: [], fileSystem: fileSystem);
            PathResolver resolver = new(config, fileSystem);
            ResolverHolder resolverHolder = new(fileSystem) { Current = resolver };

            NameTable names = new();
            ScriptDatabase database = new();
            WorkspaceIndexer indexer = new(database, () => resolver, fileSystem, names);
            await indexer.IndexAsync(IndexingMode.Full, NullIndexProgressListener.Instance, CancellationToken.None);

            DocumentStore documents = new(static _ => NullInsertProvider.Instance, names);
            string path = Path.Combine(_root, @"scripts\" + openFile);
            OpenDocument document = documents.Open(
                path, openFile == "scene.gsc" ? SceneSource : MainSource, version: 1);
            documents.AnalyzeIfStale(document);

            NavigationSupport support = new(documents, database, resolverHolder);
            return await body(support, TextDocumentSelector.ForLanguage("gsc"), path);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private Task<LocationOrLocationLinks?> ImplementationAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                ImplementationHandler handler = new(support, selector);
                ImplementationParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            },
            openFile);
    }

    private Task<LocationOrLocationLinks?> TypeDefinitionAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                string api = Path.Combine(AppContext.BaseDirectory, "Api");
                TypeDefinitionHandler handler = new(
                    support, BuiltinApiSet.Load(api, GameProfile.BlackOps3), ObjectFields.Load(api), selector);

                TypeDefinitionParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
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
