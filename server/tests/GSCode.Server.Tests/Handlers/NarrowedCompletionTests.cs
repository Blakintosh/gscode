using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Literal and field completion send the workspace's vocabulary cut to what has been typed, so the
/// list they answer with is only true for that text and must say so: marked incomplete, the editor
/// asks again as the text changes instead of filtering a page a different question would have filled
/// differently. Driven through the handler, since the flag is the handler's to set.
/// </summary>
public sealed class NarrowedCompletionTests : IDisposable
{
    private readonly string _root;

    public NarrowedCompletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-narrowed-completion-test-{Guid.NewGuid():N}");
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

    private const string EventsSource =
        "#namespace ev;\nfunction fire()\n{\n    self notify( \"player_spawned\" );\n    level.round_number = 1;\n}\n";

    /// <summary>The completion list at the end of <paramref name="typed"/>, the caller's only statement.</summary>
    private async Task<CompletionList> CompleteAfterAsync(string typed)
    {
        File.WriteAllText(Path.Combine(_root, @"scripts\events.gsc"), EventsSource);
        string source = "#namespace caller;\nfunction run()\n{\n    " + typed + "\n}\n";
        string callerPath = Path.Combine(_root, @"scripts\caller.gsc");
        File.WriteAllText(callerPath, source);

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
            OpenDocument document = documents.Open(callerPath, source, version: 1);
            documents.AnalyzeIfStale(document);

            string api = Path.Combine(AppContext.BaseDirectory, "Api");
            BuiltinApiSet builtins = BuiltinApiSet.Load(api, GameProfile.BlackOps3);
            CompletionHandler handler = new(
                new NavigationSupport(documents, database, resolverHolder),
                new CompletionEngine(database, builtins, ObjectFields.Load(api)),
                new ServerSettings(),
                TextDocumentSelector.ForLanguage("gsc"),
                builtins);

            CompletionParams request = new()
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(callerPath) },
                Position = new Position(3, 4 + typed.Length),
            };

            return await handler.Handle(request, CancellationToken.None);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    [Fact]
    public async Task ALiteralListIsIncomplete()
    {
        CompletionList list = await CompleteAfterAsync("x = \"player");

        Assert.Contains(list.Items, item => item.Label == "player_spawned");
        Assert.True(list.IsIncomplete);
    }

    [Fact]
    public async Task AFieldListIsIncomplete()
    {
        CompletionList list = await CompleteAfterAsync("x = level.rou");

        Assert.Contains(list.Items, item => item.Label == "round_number");
        Assert.True(list.IsIncomplete);
    }

    [Fact]
    public async Task ALiteralListThatMatchedNothingIsStillIncomplete()
    {
        // No row is left to carry the flag. Answered complete, the editor would cache the empty page
        // for the rest of the string, and a backspace that widens the text again would find nothing.
        // Two characters, so the literal being typed is too short to be offered back as a name.
        CompletionList list = await CompleteAfterAsync("x = \"zq");

        Assert.Empty(list.Items);
        Assert.True(list.IsIncomplete);
    }
}
