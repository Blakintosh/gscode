using GSCode.Workspace.Documents;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

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
    private readonly ServerSettings _settings;

    public DocumentFormattingHandler(DocumentStore documents, TextDocumentSelector selector, ServerSettings settings)
    {
        _documents = documents;
        _selector = selector;
        _settings = settings;
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        // The whole document, so every edit the formatter produced is kept as-is.
        FormatOptions options = FormatOptions.From(
            (int)request.Options.TabSize, request.Options.InsertSpaces, _settings);

        if ( FormattingSupport.Prepare(_documents, request.TextDocument.Uri, options) is not FormatRequest prepared
            || prepared.Edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        return Task.FromResult<TextEditContainer?>(
            new TextEditContainer(FormattingSupport.ToLspEdits(prepared.Edits)));
    }
}
