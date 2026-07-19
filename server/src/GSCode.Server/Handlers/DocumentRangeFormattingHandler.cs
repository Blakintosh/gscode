using GSCode.Core.Text;
using GSCode.Workspace.Documents;
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

    public DocumentRangeFormattingHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override DocumentRangeFormattingRegistrationOptions CreateRegistrationOptions(
        DocumentRangeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentRangeFormattingRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        GscFormatter.FormatEdit? edit = GscFormatter.FormatMinimal(document.LatestResult);
        if ( edit is null )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        TextRange requested = request.Range.ToCore();
        if ( !Overlaps(edit.Value.Range, requested) )
        {
            return Task.FromResult<TextEditContainer>(new TextEditContainer());
        }

        TextEdit textEdit = new()
        {
            Range = edit.Value.Range.ToLsp(),
            NewText = edit.Value.NewText,
        };

        return Task.FromResult<TextEditContainer>(new TextEditContainer(textEdit));
    }

    private static bool Overlaps(TextRange edit, TextRange requested)
    {
        return edit.Start <= requested.End && requested.Start <= edit.End;
    }
}
