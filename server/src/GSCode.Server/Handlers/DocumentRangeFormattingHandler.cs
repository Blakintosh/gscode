using GSCode.Workspace.Api;
using GSCode.Core.Text;
using GSCode.Workspace.Documents;
using GSCode.Server.Configuration;
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
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;
    private readonly ServerSettings _settings;
    private readonly ResolverHolder _resolver;
    private readonly StockScripts _stockScripts;

    public DocumentRangeFormattingHandler(
        DocumentStore documents, TextDocumentSelector selector, ServerSettings settings,
        ResolverHolder resolver, StockScripts stockScripts)
    {
        _resolver = resolver;
        _stockScripts = stockScripts;
        _documents = documents;
        _selector = selector;
        _settings = settings;
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
        FormatOptions options = FormatOptions.From(
            (int)request.Options.TabSize, request.Options.InsertSpaces, _settings) with { SortDirectives = false };

        if ( FormattingSupport.Prepare(_documents, _resolver, _stockScripts, request.TextDocument.Uri, options) is not FormatRequest prepared
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
