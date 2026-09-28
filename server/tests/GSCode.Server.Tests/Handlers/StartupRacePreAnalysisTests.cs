using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// Folding and expand-selection, answered BEFORE any analysis of the document has published —
/// the same startup-indexing race <see cref="DocumentSymbolNamelessTests.OutlinePopulatesBeforeAnyAnalysisHasEverPublished"/>
/// covers for the outline. A file opened while startup indexing is still running has its own
/// first analysis queued behind the indexer's thread-pool work, so any of these requests can
/// arrive while <c>OpenDocument.Analysis</c> is still null. Both handlers used to read that
/// cached snapshot via <c>DocumentStore.TryGetAnalyzed</c> and answer null when it was not there
/// yet, and neither request has a client-side "ask again" the way hover or go-to-definition do.
/// </summary>
public class StartupRacePreAnalysisTests
{
    private static readonly string Path = @"c:\bo3\share\raw\scripts\main.gsc";
    private const string Source = "function f()\n{\n    x = 1;\n}\n";

    private static DocumentStore FreshlyOpenedDocument(out OpenDocument document)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        document = documents.Open(Path, Source, version: 1);
        // Deliberately no Analyze/AnalyzeIfStale call — document.Analysis is null, exactly the
        // window between didOpen returning and its queued analysis actually finishing.
        Assert.Null(document.Analysis);
        return documents;
    }

    [Fact]
    public async Task Folding_PopulatesBeforeAnyAnalysisHasEverPublished()
    {
        DocumentStore documents = FreshlyOpenedDocument(out _);
        FoldingRangeHandler handler = new(
            documents, new TextDocumentSelector(new TextDocumentFilter { Pattern = "**/*.gsc" }));

        Container<FoldingRange>? ranges = await handler.Handle(
            new FoldingRangeRequestParam { TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(Path)) },
            CancellationToken.None);

        Assert.NotNull(ranges);
        // The function body braces fold — proof the handler actually parsed, not just that it
        // avoided throwing.
        Assert.Contains(ranges, range => range.StartLine == 0);
    }

    [Fact]
    public async Task SelectionRange_PopulatesBeforeAnyAnalysisHasEverPublished()
    {
        DocumentStore documents = FreshlyOpenedDocument(out _);
        SelectionRangeHandler handler = new(
            documents, new TextDocumentSelector(new TextDocumentFilter { Pattern = "**/*.gsc" }));

        Container<SelectionRange>? ranges = await handler.Handle(
            new SelectionRangeParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(Path)),
                Positions = new Container<LspPosition>(new LspPosition(2, 8)),
            },
            CancellationToken.None);

        Assert.NotNull(ranges);
        SelectionRange range = Assert.Single(ranges);
        // A real AST chain at that position, not just an empty fallback — proof of an actual parse.
        Assert.NotNull(range.Parent);
    }
}
