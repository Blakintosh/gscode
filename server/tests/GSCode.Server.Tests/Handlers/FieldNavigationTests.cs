using GSCode.Server.Handlers;
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
public sealed class FieldNavigationTests
{
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
    //  18,10   `craftable_shield_grab` — a compound UPDATE, which is a write but not an
    //                                   implementation
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
        + "}\n"
        + "function bump()\n"
        + "{\n"
        + "    level.craftable_shield_grab += 1;\n"
        + "}\n";

    /// <summary>
    /// One indexed workspace with both files in it, handed to whichever handler the test wants.
    /// Indexed for real: a write the store has not seen is a write no handler can report.
    /// </summary>
    private static async Task<T> WithWorkspaceAsync<T>(
        Func<HandlerWorkspace, TextDocumentIdentifier, Task<T>> body)
    {
        using HandlerWorkspace workspace = await HandlerWorkspace.BuildAsync(
        [
            new TestFile(@"scripts\scene.gsc", SceneSource),
            new TestFile(@"scripts\fields.gsc", FieldsSource),
        ]);
        workspace.Open(@"scripts\fields.gsc");

        return await body(workspace, HandlerWorkspace.Identify(@"scripts\fields.gsc"));
    }

    private static Task<LocationOrLocationLinks?> DefinitionAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                DefinitionHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
                DefinitionParams request = new()
                {
                    TextDocument = document,
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private static Task<DocumentHighlightContainer?> HighlightsAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                DocumentHighlightHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
                DocumentHighlightParams request = new()
                {
                    TextDocument = document,
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private static Task<LocationOrLocationLinks?> TypeDefinitionAtAsync(int line, int character)
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
            });
    }

    private static Task<LocationOrLocationLinks?> ImplementationAtAsync(int line, int character)
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
            });
    }

    private static Task<Container<CallHierarchyItem>?> CallHierarchyAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                CallHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
                CallHierarchyPrepareParams request = new()
                {
                    TextDocument = document,
                    Position = new Position(line, character),
                };

                return handler.Handle(request, CancellationToken.None);
            });
    }

    private static Task<Container<TypeHierarchyItem>?> TypeHierarchyAtAsync(int line, int character)
    {
        return WithWorkspaceAsync(
            (workspace, document) =>
            {
                TypeHierarchyHandler handler = new(workspace.Navigation, HandlerWorkspace.Selector);
                TypeHierarchyPrepareParams request = new()
                {
                    TextDocument = document,
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
        // one. Every write answers — the compound update at 18 included, since this request asks
        // where the field is SET — and the read the cursor is sitting on does not.
        LocationOrLocationLinks? result = await DefinitionAtAsync(13, 17);

        List<Location> locations = Locations(result);
        Assert.Equal(3, locations.Count);

        List<int> lines = [.. locations.Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([4, 14, 18], lines);
        Assert.All(locations, static location => Assert.Equal(10, location.Range.Start.Character));
    }

    [Fact]
    public async Task GoToDefinitionOnAFieldWriteStillFindsTheWrites()
    {
        // A cursor already ON a write is the ordinary way the request is made after the first jump,
        // and it must not become a special case that answers nothing.
        LocationOrLocationLinks? result = await DefinitionAtAsync(4, 10);

        Assert.Equal(3, Locations(result).Count);
    }

    [Fact]
    public async Task AFieldWriteHighlightsAsAWriteAndAReadAsARead()
    {
        DocumentHighlightContainer? result = await HighlightsAtAsync(13, 17);

        Assert.NotNull(result);
        List<DocumentHighlight> highlights = [.. result!];
        Assert.Equal(4, highlights.Count);

        foreach ( DocumentHighlight highlight in highlights )
        {
            // 18 is `+= 1`, which an editor colours as a write like any other. The kind that
            // separates it from a plain assignment is go-to-implementation's business, not this
            // handler's.
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
    public async Task ImplementationOnACallbackFieldFindsTheWriteAndTheBoundFunction()
    {
        // Both halves. The declaration is the destination worth having — nothing in the syntax at
        // `level.callback` names `on_damage` — and the assignment says WHICH write installed it,
        // which is the other half of the answer once more than one game mode binds the field.
        LocationOrLocationLinks? result = await ImplementationAtAsync(6, 10);

        List<int> lines = [.. Locations(result).Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([6, 8], lines);
    }

    [Fact]
    public async Task ImplementationOnAFieldHoldingAnInstanceFindsItsAssignment()
    {
        // A class in a field contributes no second location — its methods are a different
        // question, and go-to-type-definition is the request that asks it — but the assignment
        // that put it there is still what the field IS.
        LocationOrLocationLinks? result = await ImplementationAtAsync(5, 10);

        Location location = Assert.Single(Locations(result));
        Assert.Equal(5, location.Range.Start.Line);
        Assert.Equal(10, location.Range.Start.Character);
    }

    [Fact]
    public async Task ImplementationOnAPlainDataFieldFindsItsAssignments()
    {
        // No function, no class, just a number — and the plain assignments are still the answer to
        // "what is in this field".
        LocationOrLocationLinks? result = await ImplementationAtAsync(13, 17);

        List<int> lines = [.. Locations(result).Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([4, 14], lines);
    }

    [Fact]
    public async Task ImplementationSkipsACompoundUpdate()
    {
        // The line this request draws against go-to-definition, which lists 4, 14 AND 18.
        // `level.craftable_shield_grab += 1` adjusts what line 4 decided; it does not decide
        // anything itself, so it is a step rather than an answer.
        LocationOrLocationLinks? result = await ImplementationAtAsync(13, 17);

        Assert.DoesNotContain(Locations(result), static location => location.Range.Start.Line == 18);
    }

    [Fact]
    public async Task ImplementationOnACompoundUpdateStillAnswersWithThePlainWrites()
    {
        // A cursor ON the `+=` asks about the same field. Excluding the update from the ANSWER is
        // not the same as refusing to answer when the question is asked from there.
        LocationOrLocationLinks? result = await ImplementationAtAsync(18, 10);

        List<int> lines = [.. Locations(result).Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([4, 14], lines);
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