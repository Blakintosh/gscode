using GSCode.Core.Text;
using GSCode.Server.Formatting;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>
/// Range ("Format Selection") formatting. GSC formatting is holistic (whitespace-only, whole
/// document), so this runs the same formatter and returns the minimal edit only when the
/// changed region overlaps the requested range — a clean selection then does nothing.
/// </summary>
public sealed class DocumentRangeFormattingHandler : DocumentRangeFormattingHandlerBase
{
    private readonly FormattingSupport _formatting;
    private readonly TextDocumentSelector _selector;

    public DocumentRangeFormattingHandler(FormattingSupport formatting, TextDocumentSelector selector)
    {
        _formatting = formatting;
        _selector = selector;
    }

    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentRangeFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
    {
        // Same reasoning as the on-type handler: a fragment format must not move the file's
        // directive block. Alignment is left to the setting.
        FormatOptions options = _formatting.OptionsFor(request.Options) with { SortDirectives = false };

        if ( _formatting.Prepare(request.TextDocument.Uri, options, cancellationToken) is not FormatRequest prepared
            || prepared.Edits.IsEmpty )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        // Only the edits that touch the selection; a clean selection then does nothing.
        TextRange requested = request.Range.ToCore();
        List<TextEdit> textEdits = FormattingSupport.ToLspEdits(
            prepared.Edits.Where(edit => edit.Range.Overlaps(requested)));

        return Task.FromResult<TextEditContainer>(new TextEditContainer(textEdits));
    }
}
