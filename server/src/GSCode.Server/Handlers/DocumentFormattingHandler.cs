using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Workspace.Documents;
using GSCode.Server.Formatting;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Position = GSCode.Core.Text.Position;

namespace GSCode.Server.Handlers;

/// <summary>
/// Whole-document formatting. Runs GscFormatter over the open document and, when it produces
/// a change, returns a single full-range text edit. Refused formatting (syntax errors or an
/// unsafe reflow) yields no edits.
/// </summary>
public sealed class DocumentFormattingHandler : DocumentFormattingHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    public DocumentFormattingHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        ParseResult result = document.LatestResult;
        string? formatted = GscFormatter.Format(result);
        if ( formatted is null || string.Equals(formatted, result.Text.Text, StringComparison.Ordinal) )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        // One edit replacing the whole document with the formatted text.
        Position end = result.Text.GetPosition(result.Text.Length);
        TextRange whole = new(Position.Zero, end);
        TextEdit edit = new()
        {
            Range = whole.ToLsp(),
            NewText = formatted,
        };

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(edit));
    }
}
