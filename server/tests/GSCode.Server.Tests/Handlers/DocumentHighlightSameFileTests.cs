using GSCode.Core;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Database;
using GSCode.Parser.Preprocessing;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Document highlight is scoped to ONE file by definition, but it used to answer that question by
/// scanning this file's own <c>Extraction.References</c> for a raw key match rather than going
/// through the shared <c>FindAllReferences</c> query every other reference-shaped feature uses.
/// That missed the one thing the shared query does that a raw comparison cannot:
/// <c>MethodResolution.Canonicalize</c> widening a method key to its declaring class. This pins the
/// SCOPING half of the fix — a call in another file must never appear in a highlight list — using a
/// real two-file workspace, since references only exist once real parsing produces them.
/// </summary>
public sealed class DocumentHighlightSameFileTests : IDisposable
{
    private readonly string _root;

    public DocumentHighlightSameFileTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"gscode-highlight-scope-test-{Guid.NewGuid():N}");
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

    private void WriteFile(string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(_root, relativePath), content);
    }

    private const string LibSource = "#namespace lib;\nfunction helper()\n{\n}\n";

    // "helper();" call sits on line 3; the name starts at column 4.
    private const string CallerSource =
        "#using scripts\\lib;\n#namespace caller;\nfunction run()\n{\n    helper();\n}\n";

    private const string OtherCallerSource =
        "#using scripts\\lib;\n#namespace other;\nfunction run2()\n{\n    helper();\n}\n";

    [Fact]
    public async Task AHighlightNeverIncludesACallFromAnotherFile()
    {
        WriteFile(@"scripts\lib.gsc", LibSource);
        WriteFile(@"scripts\caller.gsc", CallerSource);
        WriteFile(@"scripts\other_caller.gsc", OtherCallerSource);

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

            // Real indexing over all three files, so both the caller's and the other file's calls
            // to helper() land in the database as real, parser-produced references.
            await indexer.IndexAsync(IndexingMode.Partial, NullIndexProgressListener.Instance, CancellationToken.None);

            DocumentStore documents = new(static _ => NullInsertProvider.Instance, names);
            NavigationSupport support = new(documents, database, resolverHolder);

            string callerPath = Path.Combine(_root, @"scripts\caller.gsc");

            // Opened and analysed with the SAME text just indexed from disk, so the cursor position
            // below lands on the same call the index already knows about.
            OpenDocument document = documents.Open(callerPath, CallerSource, version: 1);
            documents.AnalyzeIfStale(document);

            DocumentHighlightHandler handler = new(support, TextDocumentSelector.ForLanguage("gsc"));

            DocumentHighlightParams request = new()
            {
                TextDocument = new TextDocumentIdentifier { Uri = DocumentUri.FromFileSystemPath(callerPath) },
                Position = new Position(4, 6), // inside "helper" on line 5 (0-based line 4)
            };

            DocumentHighlightContainer? result = await handler.Handle(request, CancellationToken.None);

            Assert.NotNull(result);
            DocumentHighlight highlight = Assert.Single(result!);
            Assert.Equal(4, highlight.Range.Start.Line);
        }
        finally
        {
            GameProfile.Select(previous.ShortName);
        }
    }
}
