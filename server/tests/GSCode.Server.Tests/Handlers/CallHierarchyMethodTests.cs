using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Tests.Corpus;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The call hierarchy on a class METHOD, prepared from the editor and expanded both ways.
///
/// A method's key carries its owner class, and every call to it is indexed under that key. An item
/// is handed back to the client and returned on the next request, so whatever the key needs has to
/// survive that round trip — an item that lost the owner asked the next request for a free function
/// of the same name, which nothing calls. And the outgoing half looked declarations up as free
/// functions, which finds no method at all.
/// </summary>
[Collection(GameProfileCollection.Name)]
public sealed class CallHierarchyMethodTests : IDisposable
{
    private readonly string _root;

    public CallHierarchyMethodTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-call-hierarchy-method-{Guid.NewGuid():N}");
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

    // `helper` is declared at line 3, character 13; `run` at line 6, character 13, and calls helper
    // on line 8.
    private const string ThingSource =
        "#namespace thing;\n"
        + "class cThing\n"
        + "{\n"
        + "    function helper()\n"
        + "    {\n"
        + "    }\n"
        + "    function run()\n"
        + "    {\n"
        + "        [[ self ]]->helper();\n"
        + "    }\n"
        + "}\n";

    /// <summary>
    /// Indexes the one file for real, opens it, prepares the hierarchy at a position and hands the
    /// single prepared item to the test with the handler that made it.
    /// </summary>
    private async Task<T> PreparedAtAsync<T>(
        int line, int character, Func<CallHierarchyHandler, CallHierarchyItem, Task<T>> body)
    {
        string path = Path.Combine(_root, "scripts", "thing.gsc");
        File.WriteAllText(path, ThingSource);

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
            OpenDocument document = documents.Open(path, ThingSource, version: 1);
            documents.AnalyzeIfStale(document);

            CallHierarchyHandler handler = new(
                new NavigationSupport(documents, database, resolverHolder), TextDocumentSelector.ForLanguage("gsc"));

            Container<CallHierarchyItem>? prepared = await handler.Handle(
                new CallHierarchyPrepareParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(path) },
                    Position = new Position(line, character),
                },
                CancellationToken.None);

            Assert.NotNull(prepared);
            return await body(handler, Assert.Single(prepared));
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }

    [Fact]
    public async Task AMethodExpandsToTheMethodThatCallsIt()
    {
        CallHierarchyItem caller = await PreparedAtAsync(3, 13, async (handler, helper) =>
        {
            Assert.Equal("helper", helper.Name);

            Container<CallHierarchyIncomingCall>? incoming = await handler.Handle(
                new CallHierarchyIncomingCallsParams { Item = helper }, CancellationToken.None);

            Assert.NotNull(incoming);
            return Assert.Single(incoming).From;
        });

        Assert.Equal("run", caller.Name);
    }

    [Fact]
    public async Task AMethodExpandsToTheMethodItCalls()
    {
        CallHierarchyItem callee = await PreparedAtAsync(6, 13, async (handler, run) =>
        {
            Assert.Equal("run", run.Name);

            Container<CallHierarchyOutgoingCall>? outgoing = await handler.Handle(
                new CallHierarchyOutgoingCallsParams { Item = run }, CancellationToken.None);

            Assert.NotNull(outgoing);
            return Assert.Single(outgoing).To;
        });

        Assert.Equal("helper", callee.Name);
    }
}
