using GSCode.Core;
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

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// An indexed in-memory workspace wired the way <c>ServerServices</c> wires the server, for tests
/// that drive a handler. Every script lives under <see cref="TestPaths.RawRoot"/>, and every piece
/// that takes a game is handed the same one — the indexer, the builtin library, the object fields
/// AND <see cref="GameProfile.Active"/>, which the handlers read for themselves. A workspace whose
/// indexer and Active disagree fails silently, so there is one field and it goes everywhere.
///
/// Dispose it: the scope on Active is held for the life of the workspace, because a handler reads
/// Active while it answers, long after indexing is done.
/// </summary>
public sealed class HandlerWorkspace : IDisposable
{
    /// <summary>The selector every handler here is registered with.</summary>
    public static TextDocumentSelector Selector { get; } = TextDocumentSelector.ForLanguage("gsc");

    private readonly ProfileScope _profileScope;

    private HandlerWorkspace(ProfileScope profileScope, GameProfile profile, FakeFileSystem files)
    {
        _profileScope = profileScope;
        Profile = profile;
        Files = files;

        Resolver = new PathResolver(TestPaths.Config(files), files);
        ResolverHolder = new ResolverHolder(files) { Current = Resolver };

        Names = new NameTable();
        Database = new ScriptDatabase();
        Inserts = new InsertCache();
        Indexer = new WorkspaceIndexer(Database, () => ResolverHolder.Current, files, Names, Inserts, profile);

        // The server's own factory: a real insert provider per file, sharing one header cache, so
        // an #insert in a test source resolves exactly as it does in the editor.
        Documents = new DocumentStore(
            path =>
            {
                PathResolver current = ResolverHolder.Current;
                return new ResolverInsertProvider(current, current.GetContext(path), files, Inserts);
            },
            Names,
            Inserts);

        string api = Path.Combine(AppContext.BaseDirectory, "Api");
        Builtins = BuiltinApiSet.Load(api, profile);
        ObjectFields = ObjectFields.Load(api, profile);
        Navigation = new NavigationSupport(Documents, Database, ResolverHolder, Builtins);
        Completion = new CompletionEngine(Database, Builtins, ObjectFields);
    }

    public GameProfile Profile { get; }

    public FakeFileSystem Files { get; }

    public PathResolver Resolver { get; }

    public ResolverHolder ResolverHolder { get; }

    public NameTable Names { get; }

    public ScriptDatabase Database { get; }

    public InsertCache Inserts { get; }

    public WorkspaceIndexer Indexer { get; }

    public DocumentStore Documents { get; }

    public BuiltinApiSet Builtins { get; }

    public ObjectFields ObjectFields { get; }

    public NavigationSupport Navigation { get; }

    public CompletionEngine Completion { get; }

    /// <summary>
    /// Writes <paramref name="files"/> under the raw root and indexes them under
    /// <paramref name="profile"/>, or <see cref="ProfileScope.Default"/> when the game is not what
    /// the test is about.
    /// </summary>
    public static async Task<HandlerWorkspace> BuildAsync(
        IEnumerable<TestFile> files, GameProfile? profile = null, IndexingMode mode = IndexingMode.Full)
    {
        GameProfile chosen = profile ?? ProfileScope.Default;
        ProfileScope scope = ProfileScope.Use(chosen);
        try
        {
            FakeFileSystem fileSystem = new();
            foreach ( TestFile file in files )
            {
                fileSystem.AddFile(TestPaths.Raw(file.RelativePath), file.Text);
            }

            HandlerWorkspace workspace = new(scope, chosen, fileSystem);
            await workspace.Indexer.IndexAsync(mode, NullIndexProgressListener.Instance, CancellationToken.None);
            return workspace;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a script the way the editor does — with the text it has on disk — and analyses it, so
    /// a position in the test source lands on what the index already knows about.
    /// </summary>
    public OpenDocument Open(string relativePath)
    {
        return Open(relativePath, Files.ReadAllText(TestPaths.Raw(relativePath)));
    }

    /// <summary>
    /// Opens and analyses a buffer the index has NOT seen: a file being written that is not on disk
    /// yet, or an edit that has not been saved. That is a different question from one the index
    /// already answers, and the tests that ask it keep the file out of the workspace on purpose.
    /// </summary>
    public OpenDocument Open(string relativePath, string text)
    {
        OpenDocument document = Documents.Open(TestPaths.Raw(relativePath), text, version: 1);
        Documents.AnalyzeIfStale(document);
        return document;
    }

    /// <summary>The protocol's name for a script under the raw root.</summary>
    public static TextDocumentIdentifier Identify(string relativePath)
    {
        return new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(TestPaths.Raw(relativePath)) };
    }

    public void Dispose()
    {
        _profileScope.Dispose();
    }
}
