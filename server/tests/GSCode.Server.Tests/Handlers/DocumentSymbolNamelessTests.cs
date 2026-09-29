using GSCode.Core;
using GSCode.Parser.Preprocessing;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace GSCode.Server.Tests.Handlers;

/// <summary>
/// The outline is rebuilt on every keystroke, so it sees the file mid-declaration constantly. LSP
/// forbids an empty DocumentSymbol name, and the whole request fails on one — which is why typing
/// the word `function` raised "Request textDocument/documentSymbol failed. Error: name must not be
/// falsy" and took the entire outline down with it.
///
/// Being nameless is the normal intermediate state of a file being written, not a fault, so these
/// pin that a half-typed declaration is dropped and everything already written survives.
/// </summary>
public class DocumentSymbolNamelessTests
{
    private static readonly string s_path = TestPaths.Raw(@"scripts\main.gsc");

    private static async Task<List<DocumentSymbol>> OutlineAsync(string text)
    {
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(s_path, text, version: 1);
        documents.Analyze(document);

        DocumentSymbolHandler handler = new(
            documents,
            new ServerSettings(),
            new TextDocumentSelector(new TextDocumentFilter { Pattern = "**/*.gsc" }));

        SymbolInformationOrDocumentSymbolContainer? container = await handler.Handle(
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(s_path)) },
            CancellationToken.None);

        List<DocumentSymbol> symbols = [];
        foreach ( SymbolInformationOrDocumentSymbol entry in container ?? [] )
        {
            if ( entry.DocumentSymbol is not null )
            {
                symbols.Add(entry.DocumentSymbol);
            }
        }

        return symbols;
    }

    [Fact]
    public async Task TypingTheFunctionKeywordAloneDoesNotThrow()
    {
        // The exact reported keystroke: the keyword is complete, the name is not yet typed.
        List<DocumentSymbol> symbols = await OutlineAsync("function ");

        Assert.DoesNotContain(symbols, static symbol => string.IsNullOrWhiteSpace(symbol.Name));
    }

    [Fact]
    public async Task AHalfTypedDeclarationDoesNotHideTheRestOfTheFile()
    {
        // The failure was whole-request, so the cost of one nameless symbol was every OTHER symbol
        // vanishing from the outline at the same time. Dropping it must be all that happens.
        List<DocumentSymbol> symbols = await OutlineAsync(
            "#namespace vibing3;\nfunction alreadyWritten()\n{\n}\nfunction ");

        Assert.NotEmpty(symbols);
        Assert.Contains(Names(symbols), static name => name == "alreadyWritten");
        Assert.DoesNotContain(Names(symbols), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public async Task OutlinePopulatesBeforeAnyAnalysisHasEverPublished()
    {
        // The startup-indexing race: didOpen's own first analysis runs off the request thread
        // (TextSyncHandler.ScheduleImmediateAnalysis), queued behind the indexer's thread-pool
        // work, so a documentSymbol request can arrive before OpenDocument.Analysis is anything
        // but null. The handler used to answer null there via TryGetAnalyzed's cached-snapshot
        // read, and LSP has no "ask again" for document symbols, so the outline stayed empty until
        // the user typed something. Deliberately no Analyze/AnalyzeIfStale call before the request
        // — this is exactly that race, and the handler must parse for itself instead of trusting a
        // snapshot that has not been published yet.
        DocumentStore documents = new(static _ => NullInsertProvider.Instance, new NameTable());
        OpenDocument document = documents.Open(s_path, "#namespace vibing3;\nfunction one()\n{\n}\n", version: 1);
        Assert.Null(document.Analysis);

        DocumentSymbolHandler handler = new(
            documents,
            new ServerSettings(),
            new TextDocumentSelector(new TextDocumentFilter { Pattern = "**/*.gsc" }));

        SymbolInformationOrDocumentSymbolContainer? container = await handler.Handle(
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(s_path)) },
            CancellationToken.None);

        List<DocumentSymbol> symbols = [];
        foreach ( SymbolInformationOrDocumentSymbol entry in container ?? [] )
        {
            if ( entry.DocumentSymbol is not null )
            {
                symbols.Add(entry.DocumentSymbol);
            }
        }

        Assert.Contains(Names(symbols), static name => name == "one");
    }

    [Fact]
    public async Task ACompleteFileIsUnchanged()
    {
        // The guard must not cost anything in the ordinary case.
        List<DocumentSymbol> symbols = await OutlineAsync(
            "#namespace vibing3;\nfunction one()\n{\n}\nfunction two()\n{\n}\n");

        List<string> names = Names(symbols);
        Assert.Contains(names, static name => name == "one");
        Assert.Contains(names, static name => name == "two");
    }

    /// <summary>Every name in the tree, so a nameless child cannot hide behind a named parent.</summary>
    private static List<string> Names(IEnumerable<DocumentSymbol> symbols)
    {
        List<string> names = [];
        foreach ( DocumentSymbol symbol in symbols )
        {
            names.Add(symbol.Name);
            if ( symbol.Children is not null )
            {
                names.AddRange(Names(symbol.Children));
            }
        }

        return names;
    }
}
