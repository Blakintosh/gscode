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
/// Navigation from a class <c>var</c>, read the way BO3's own scripts read one: as a BARE NAME
/// inside the class body, never through <c>self.</c>.
///
/// That spelling is the whole difficulty. The thing at the cursor looks exactly like a local, and
/// before this every request answered nothing at all for it — not go-to-definition, not
/// find-references, not even a hover, and not on the <c>var</c> declaration itself. It is the
/// mirror of the plain-field problem: a field has no declaration and had to be answered from its
/// writes, while a member HAS one and simply was not connected to the name at the cursor.
///
/// A member resolves two different ways depending on where its <c>var</c> is, and both are
/// exercised here because only the first is available to a single file's parse:
///
/// <list type="bullet">
/// <item>DECLARED IN THIS FILE (or an ancestor this file declares) — extraction keys the use, so
/// every surface works including find-references and rename. 199 of BO3's 206 <c>var</c>s.</item>
/// <item>DECLARED IN AN ANCESTOR ELSEWHERE — extraction cannot see the declaration, so the use is
/// unindexed and <c>NavigationSupport</c> resolves it at the cursor instead. The cursor-outward
/// requests work; rename REFUSES, because the uses it cannot see are uses it would leave spelling
/// the old name. The other 7, which is what <c>scene_shared.gsc</c> is made of.</item>
/// </list>
/// </summary>
public sealed class ClassMemberNavigationTests : IDisposable
{
    private readonly string _root;

    public ClassMemberNavigationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-member-nav-test-{Guid.NewGuid():N}");
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

    // Landmarks, 0-based:
    //   3,8    `var _shared;`  — declared here, USED from the other file too
    //   4,8    `var _own;`     — declared and used only here
    //   7,8    `_shared = 1;`  — a write
    //  11,8    `_own = 2;`     — a write
    //  12,12   `_own`          — a read
    private const string BaseSource =
        "#namespace basens;\n"
        + "class cBase\n"
        + "{\n"
        + "    var _shared;\n"
        + "    var _own;\n"
        + "    constructor()\n"
        + "    {\n"
        + "        _shared = 1;\n"
        + "    }\n"
        + "    function useOwn()\n"
        + "    {\n"
        + "        _own = 2;\n"
        + "        x = _own;\n"
        + "    }\n"
        + "}\n";

    // Landmarks, 0-based:
    //   4,8    `var _mine;`    — this class's own
    //   7,8    `_mine = 3;`    — a write
    //   8,8    `_shared = 4;`  — a write to the INHERITED member, declared in the other file
    //   9,12   `_mine`         — a read
    //   9,20   `_shared`       — a read of the inherited member
    private const string DerivedSource =
        "#using scripts\\base;\n"
        + "#namespace derivedns;\n"
        + "class cDerived : cBase\n"
        + "{\n"
        + "    var _mine;\n"
        + "    function work()\n"
        + "    {\n"
        + "        _mine = 3;\n"
        + "        _shared = 4;\n"
        + "        y = _mine + _shared;\n"
        + "    }\n"
        + "}\n";

    private async Task<T> WithWorkspaceAsync<T>(
        string openFile, Func<NavigationSupport, TextDocumentSelector, string, Task<T>> body)
    {
        File.WriteAllText(Path.Combine(_root, @"scripts\base.gsc"), BaseSource);
        File.WriteAllText(Path.Combine(_root, @"scripts\derived.gsc"), DerivedSource);

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
                path, openFile == "base.gsc" ? BaseSource : DerivedSource, version: 1);
            documents.AnalyzeIfStale(document);

            NavigationSupport support = new(documents, database, resolverHolder);
            return await body(support, TextDocumentSelector.ForLanguage("gsc"), path);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    private Task<LocationOrLocationLinks?> DefinitionAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
            new DefinitionHandler(support, selector).Handle(
                new DefinitionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                },
                CancellationToken.None));
    }

    private Task<LocationOrLocationLinks?> ImplementationAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
            new ImplementationHandler(support, selector).Handle(
                new ImplementationParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                },
                CancellationToken.None));
    }

    private Task<LocationContainer?> ReferencesAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
            new ReferencesHandler(support, selector).Handle(
                new ReferenceParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                    Context = new ReferenceContext { IncludeDeclaration = true },
                },
                CancellationToken.None));
    }

    private Task<DocumentHighlightContainer?> HighlightsAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
            new DocumentHighlightHandler(support, selector).Handle(
                new DocumentHighlightParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                },
                CancellationToken.None));
    }

    private Task<RangeOrPlaceholderRange?> PrepareRenameAtAsync(string openFile, int line, int character)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
        {
            string api = Path.Combine(AppContext.BaseDirectory, "Api");
            PrepareRenameHandler handler = new(
                support, BuiltinApiSet.Load(api, GameProfile.BlackOps3), ObjectFields.Load(api), selector);

            return handler.Handle(
                new PrepareRenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                },
                CancellationToken.None);
        });
    }

    private Task<WorkspaceEdit?> RenameAtAsync(string openFile, int line, int character, string newName)
    {
        return WithWorkspaceAsync(openFile, (support, selector, path) =>
        {
            string api = Path.Combine(AppContext.BaseDirectory, "Api");
            RenameHandler handler = new(
                support, BuiltinApiSet.Load(api, GameProfile.BlackOps3), ObjectFields.Load(api), selector);

            return handler.Handle(
                new RenameParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                    NewName = newName,
                },
                CancellationToken.None);
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
    public async Task GoToDefinitionOnABareMemberFindsItsVarDeclaration()
    {
        // The read at `x = _own;`. Unlike a plain field this has a real declaration to land on, so
        // there is no writes-as-definition special case here — the `var` is the answer.
        LocationOrLocationLinks? result = await DefinitionAtAsync("base.gsc", 12, 12);

        Location location = Assert.Single(Locations(result));
        Assert.Contains("base.gsc", location.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, location.Range.Start.Line);
    }

    [Fact]
    public async Task GoToDefinitionOnTheVarDeclarationItselfAnswers()
    {
        // It answered nothing before, which made the feature look broken at the one place a reader
        // is most certain they are on a symbol.
        LocationOrLocationLinks? result = await DefinitionAtAsync("base.gsc", 4, 8);

        Assert.Equal(4, Assert.Single(Locations(result)).Range.Start.Line);
    }

    [Fact]
    public async Task ImplementationOnABareMemberFindsItsAssignments()
    {
        LocationOrLocationLinks? result = await ImplementationAtAsync("base.gsc", 12, 12);

        Location location = Assert.Single(Locations(result));
        Assert.Equal(11, location.Range.Start.Line);
    }

    [Fact]
    public async Task FindReferencesOnAMemberCoversTheDeclarationAndBothUses()
    {
        LocationContainer? result = await ReferencesAtAsync("base.gsc", 12, 12);

        Assert.NotNull(result);
        List<int> lines = [.. result!.Select(static location => location.Range.Start.Line).Order()];
        Assert.Equal([4, 11, 12], lines);
    }

    [Fact]
    public async Task AMemberWriteHighlightsAsAWrite()
    {
        DocumentHighlightContainer? result = await HighlightsAtAsync("base.gsc", 12, 12);

        Assert.NotNull(result);
        List<DocumentHighlight> highlights = [.. result!];
        Assert.Equal(3, highlights.Count);

        Assert.Equal(
            DocumentHighlightKind.Write,
            Assert.Single(highlights, h => h.Range.Start.Line == 11).Kind);

        Assert.Equal(
            DocumentHighlightKind.Read,
            Assert.Single(highlights, h => h.Range.Start.Line == 12).Kind);
    }

    [Fact]
    public async Task AMemberWhoseHierarchyIsOneFileCanBeRenamed()
    {
        // `_mine` is cDerived's own and cDerived has no subclass, so every use of it is indexed.
        WorkspaceEdit? result = await RenameAtAsync("derived.gsc", 9, 12, "_renamed");

        Assert.NotNull(result);
        Assert.NotNull(result!.Changes);
        List<TextEdit> edits = [.. result.Changes!.Values.SelectMany(static e => e)];
        Assert.Equal(3, edits.Count);
    }

    [Fact]
    public async Task GoToDefinitionFollowsAMemberInheritedFromAnotherFile()
    {
        // `_shared` is declared in base.gsc and read bare in derived.gsc's method. Extraction
        // cannot key that use — one file's parse cannot see the other's `var` — so this is the
        // resolution-time path, and it is the shape scene_shared.gsc is entirely made of.
        LocationOrLocationLinks? result = await DefinitionAtAsync("derived.gsc", 9, 20);

        Location location = Assert.Single(Locations(result));
        Assert.Contains("base.gsc", location.Uri.GetFileSystemPath(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, location.Range.Start.Line);
    }

    [Fact]
    public async Task RenamingAnInheritedMemberReachesTheSubclassInTheOtherFile()
    {
        // The case the whole cross-file half exists for. `_shared` is declared in base.gsc and
        // read bare in derived.gsc, whose parse cannot see the `var` — so derived.gsc records the
        // name as a member candidate on the strength of having an ancestor it cannot see, and the
        // class graph confirms it here. Without that, this rewrote base.gsc and left derived.gsc
        // spelling the old name.
        WorkspaceEdit? result = await RenameAtAsync("base.gsc", 7, 8, "_renamed");

        Assert.NotNull(result);
        Assert.NotNull(result!.Changes);

        Dictionary<string, int> perFile = result.Changes!.ToDictionary(
            static pair => Path.GetFileName(pair.Key.GetFileSystemPath()),
            static pair => pair.Value.Count(),
            StringComparer.OrdinalIgnoreCase);

        // base.gsc: the `var` and one write. derived.gsc: one write and one read.
        Assert.Equal(2, perFile["base.gsc"]);
        Assert.Equal(2, perFile["derived.gsc"]);
    }

    [Fact]
    public async Task PrepareRenameOffersAnInheritedMember()
    {
        RangeOrPlaceholderRange? result = await PrepareRenameAtAsync("derived.gsc", 9, 20);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindReferencesOnAnInheritedMemberCrossesTheFileBoundary()
    {
        LocationContainer? result = await ReferencesAtAsync("derived.gsc", 9, 20);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Count());
    }

    [Fact]
    public async Task AnOrdinaryLocalInAClassWithAnUnseenParentIsStillALocal()
    {
        // The cost of the candidate rule, and the check that pays it back. derived.gsc records
        // EVERY bare name in cDerived as a member, `y` included, because it cannot see cBase.
        // The class graph says no ancestor declares `y`, so the hit is dropped and `y` goes down
        // the local path exactly as before — one function, not the workspace.
        WorkspaceEdit? result = await RenameAtAsync("derived.gsc", 9, 8, "_renamed");

        Assert.NotNull(result);
        List<TextEdit> edits = [.. result!.Changes!.Values.SelectMany(static e => e)];
        Assert.Single(edits);
    }
}
