using GSCode.Parser;
using GSCode.Workspace.Documents;
using GSCode.Server.Formatting;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// On-type formatting, triggered after a closing brace or semicolon. Reuses the whole-document
/// formatter and returns its minimal edit; because the formatter refuses files with syntax
/// errors, a half-typed document is simply left alone until it parses again.
/// </summary>
public sealed class DocumentOnTypeFormattingHandler : DocumentOnTypeFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    public DocumentOnTypeFormattingHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override DocumentOnTypeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentOnTypeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentOnTypeFormattingRegistrationOptions
        {
            DocumentSelector = _selector,
            FirstTriggerCharacter = "}",
            MoreTriggerCharacter = new Container<string>(";"),
        };
    }

    public override Task<TextEditContainer?> Handle(DocumentOnTypeFormattingParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // Analyse fresh. FormatMinimal diffs the formatted output against the analysed text and
        // returns a MINIMAL edit, so its range indexes into that text — applying it to a document
        // that has since changed points the range at unrelated characters and corrupts the file.
        // Every other stale read shows something wrong; this one writes something wrong.
        ParseResult analysis = _documents.AnalyzeIfStale(document);

        GscFormatter.FormatEdit? edit = GscFormatter.FormatMinimal(analysis);
        if ( edit is null )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        TextEdit textEdit = new()
        {
            Range = edit.Value.Range.ToLsp(),
            NewText = edit.Value.NewText,
        };

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(textEdit));
    }
}
