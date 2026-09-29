using GSCode.Server.Formatting;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Whole-document formatting. Runs GscFormatter over the open document and returns its per-region
/// edits (see FormatMinimalEdits) rather than one edit spanning the whole file, so the caret stays
/// put on every unchanged line. Refused formatting (syntax errors or an unsafe reflow) yields no
/// edits.
/// </summary>
public sealed class DocumentFormattingHandler : DocumentFormattingHandlerBase
{
    private readonly FormattingSupport _formatting;
    private readonly TextDocumentSelector _selector;

    public DocumentFormattingHandler(FormattingSupport formatting, TextDocumentSelector selector)
    {
        _formatting = formatting;
        _selector = selector;
    }

    protected override DocumentFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        // The whole document, so every edit the formatter produced is kept as-is.
        FormatOptions options = _formatting.OptionsFor(request.Options);

        if ( _formatting.Prepare(request.TextDocument.Uri, options, cancellationToken) is not FormatRequest prepared
            || prepared.Edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer?>(null);
        }

        return Task.FromResult<TextEditContainer?>(
            new TextEditContainer(FormattingSupport.ToLspEdits(prepared.Edits)));
    }
}
