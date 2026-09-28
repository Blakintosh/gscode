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
/// Navigation from a FIELD — <c>level.craftable_shield_grab</c> and its kin.
///
/// A field is the one symbol kind the scripts coin without declaring: there is no
/// <c>ReferenceKind.Definition</c> anywhere for one, and every navigation request that looked for
/// a declaration therefore answered nothing at all. Two different answers replace that, and the
/// distinction is the point of this file:
///
/// * WHERE the field is — its writes, which is what go-to-definition means for a name that comes
///   into existence by being assigned to;
/// * WHAT the field holds — the class or the function a write binds to it, which is what type
///   definition, implementation and the two hierarchies each ask in their own way.
///
/// Driven through the handlers over a real indexed two-file workspace, because both answers are
/// cross-file by nature: a callback is assigned in one script and invoked in another, and a
/// single-file fixture would pass while the feature stayed broken in the case that matters.
/// </summary>
public sealed class FieldNavigationTests : IDisposable
{
    private readonly string _root;

    public FieldNavigationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-field-nav-test-{Guid.NewGuid():N}");
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

    // cScene's name starts at character 6 of line 1; cAwarenessScene's at character 6 of line 7.
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

    // Line/character landmarks, all 0-based, as the tests below name them:
    //
    //   4,10   `craftable_shield_grab` — its first write
    //   5,10   `scene`                 — written with a `new cAwarenessScene()`
    //   6,10   `callback`              — written with `&on_damage`
    //   8,9    `on_damage`             — the function declaration
    //  13,17   `craftable_shield_grab` — a READ
    //  14,10   `craftable_shield_grab` — its second write
    private const string FieldsSource =
        "#using scripts\\scene;\n"
        + "#namespace fields;\n"
        + "function init()\n"
        + "{\n"
        + "    level.craftable_shield_grab = 1;\n"
        + "    level.scene = new cAwarenessScene();\n"
        + "    level.callback = &on_damage;\n"
        + "}\n"
        + "function on_damage()\n"
        + "{\n"
        + "}\n"
        + "function use()\n"
        + "{\n"
        + "    grab = level.craftable_shield_grab;\n"
        + "    level.craftable_shield_grab = 2;\n"
        + "}\n";

    /// <summary>
    /// One indexed workspace with both files written to disk, handed to whichever handler the test
    /// wants. Indexed for real: a write the store has not seen is a write no handler can report.
    /// </summary>
    private async Task<T> WithWorkspaceAsync<T>(
        Func<NavigationSupport, TextDocumentSelector, string, Task<T>> body)
    {
        File.WriteAllText(Path.Combine(_root, @"scripts\scene.gsc"), SceneSource);
        File.WriteAllText(Path.Combine(_root, @"scripts\fields.gsc"), FieldsSource);

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
            string path = Path.Combine(_root, @"scripts\fields.gsc");
            OpenDocument document = documents.Open(path, FieldsSource, version: 1);
            documents.AnalyzeIfStale(document);

            NavigationSupport support = new(documents, database, resolverHolder);
            return await body(support, TextDocumentSelector.ForLanguage("gsc"), path);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private Task<LocationOrLocationLinks?> DefinitionAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                DefinitionHandler handler = new(support, selector);
                DefinitionParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private Task<DocumentHighlightContainer?> HighlightsAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                DocumentHighlightHandler handler = new(support, selector);
                DocumentHighlightParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private Task<LocationOrLocationLinks?> TypeDefinitionAtAsync(int line, int character)
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
            });
    }

    private Task<LocationOrLocationLinks?> ImplementationAtAsync(int line, int character)
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
            });
    }

    private Task<Container<CallHierarchyItem>?> CallHierarchyAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                CallHierarchyHandler handler = new(support, selector);
                CallHierarchyPrepareParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private Task<Container<TypeHierarchyItem>?> TypeHierarchyAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (support, selector, path) =>
            {
                TypeHierarchyHandler handler = new(support, selector);
                TypeHierarchyPrepareParams request = new()
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private static List<Location> Locations(LocationOrLocationLinks? result)
    {
        Assert.NotNull(result);

        List<Location> locations = [];
        foreach ( LocationOrLocationLink entry in result! )
        {
            Assert.NotNull(entry.Location);
            locations.Add(entry.Location!);
        }

        return locations;
    }

    [Fact]
    public async Task GoToDefinitionOnAFieldReadFindsEveryWrite()
    {
        // The whole gap in one assertion: F12 on `level.craftable_shield_grab` used to return an
        // empty list, because the handler kept only Definition entries and a field never produces
        // one. Both writes answer, and the read the cursor is sitting on does not.
        LocationOrLocationLinks? result = await DefinitionAtAsync(13, 17);

        List<Location> locations = Locations(result);
        Assert.Equal(2, locations.Count);

        List<int> lines = [.. locations.Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([4, 14], lines);
        Assert.All(locations, static location => Assert.Equal(10, location.Range.Start.Character));
    }

    [Fact]
    public async Task GoToDefinitionOnAFieldWriteStillFindsTheWrites()
    {
        // A cursor already ON a write is the ordinary way the request is made after the first jump,
        // and it must not become a special case that answers nothing.
        LocationOrLocationLinks? result = await DefinitionAtAsync(4, 10);

        Assert.Equal(2, Locations(result).Count);
    }

    [Fact]
    public async Task AFieldWriteHighlightsAsAWriteAndAReadAsARead()
    {
        DocumentHighlightContainer? result = await HighlightsAtAsync(13, 17);

        Assert.NotNull(result);
        List<DocumentHighlight> highlights = [.. result!];
        Assert.Equal(3, highlights.Count);

        foreach ( DocumentHighlight highlight in highlights )
        {
            DocumentHighlightKind expected = highlight.Range.Start.Line == 13
                ? DocumentHighlightKind.Read
                : DocumentHighlightKind.Write;

            Assert.Equal(expected, highlight.Kind);
        }
    }

    [Fact]
    public async Task TypeDefinitionOnAFieldHoldingAnInstanceFindsItsClass()
    {
        // `level.scene = new cAwarenessScene()`. The class is declared in the OTHER file, which is
        // the ordinary shape: a field is written where the subsystem is set up and read everywhere
        // else.
        LocationOrLocationLinks? result = await TypeDefinitionAtAsync(5, 10);

        Location location = Assert.Single(Locations(result));
        Assert.Contains("scene.gsc", location.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, location.Range.Start.Line);
        Assert.Equal(6, location.Range.Start.Character);
    }

    [Fact]
    public async Task TypeDefinitionOnACallbackFieldFindsTheFunction()
    {
        LocationOrLocationLinks? result = await TypeDefinitionAtAsync(6, 10);

        Location location = Assert.Single(Locations(result));
        Assert.Equal(8, location.Range.Start.Line);
        Assert.Equal(9, location.Range.Start.Character);
    }

    [Fact]
    public async Task TypeDefinitionOnAFieldHoldingANumberFindsNothing()
    {
        // A number is a type, not an identity. The request must decline rather than fall back to
        // the write, which is what go-to-definition already answers with.
        LocationOrLocationLinks? result = await TypeDefinitionAtAsync(4, 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task ImplementationOnACallbackFieldFindsTheBoundFunction()
    {
        // The GSC dialect of an override: nothing in the syntax at `level.callback` names
        // `on_damage`, and without this there is no way to reach it from a call site at all.
        LocationOrLocationLinks? result = await ImplementationAtAsync(6, 10);

        Location location = Assert.Single(Locations(result));
        Assert.Equal(8, location.Range.Start.Line);
        Assert.Equal(9, location.Range.Start.Character);
    }

    [Fact]
    public async Task ImplementationOnAFieldHoldingAnInstanceFindsNothing()
    {
        // A class in a field has no implementations. Its methods are a different question, and
        // go-to-type-definition is the request that asks it.
        LocationOrLocationLinks? result = await ImplementationAtAsync(5, 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task CallHierarchyOnACallbackFieldAnchorsOnTheBoundFunction()
    {
        Container<CallHierarchyItem>? result = await CallHierarchyAtAsync(6, 10);

        Assert.NotNull(result);
        CallHierarchyItem item = Assert.Single(result!);
        Assert.Equal("on_damage", item.Name);
        Assert.Equal(8, item.SelectionRange.Start.Line);
    }

    [Fact]
    public async Task CallHierarchyOnAFieldHoldingAnInstanceFindsNothing()
    {
        // A class is not callable, and neither is the field. Anchoring on its constructor would be
        // an answer to a question nobody asked.
        Container<CallHierarchyItem>? result = await CallHierarchyAtAsync(5, 10);

        Assert.Null(result);
    }

    [Fact]
    public async Task TypeHierarchyOnAFieldAnchorsOnTheClassItHolds()
    {
        Container<TypeHierarchyItem>? result = await TypeHierarchyAtAsync(5, 10);

        Assert.NotNull(result);
        TypeHierarchyItem item = Assert.Single(result!);
        Assert.Equal("cAwarenessScene", item.Name);
        Assert.Contains("scene.gsc", item.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypeHierarchyOnACallbackFieldFindsNothing()
    {
        Container<TypeHierarchyItem>? result = await TypeHierarchyAtAsync(6, 10);

        Assert.Null(result);
    }
}
