using GSCode.Core.Symbols;
using GSCode.Workspace.Database;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Highlights every occurrence of the symbol under the cursor within the current file.</summary>
public sealed class DocumentHighlightHandler : DocumentHighlightHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public DocumentHighlightHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions { DocumentSelector = _selector };
    }

    public override Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<DocumentHighlightContainer?>(null);
        }

        PositionHit hit = SymbolAtPosition.Resolve(target.Result, request.Position.ToCore());
        if ( hit.Kind != HitKind.Reference )
        {
            return Task.FromResult<DocumentHighlightContainer?>(null);
        }

        List<DocumentHighlight> highlights = [];
        foreach ( ReferenceEntry entry in target.Result.Extraction.References )
        {
            if ( entry.Key == hit.Key )
            {
                highlights.Add(new DocumentHighlight
                {
                    Range = entry.Range.ToLsp(),
                    Kind = entry.Kind == ReferenceKind.Definition ? DocumentHighlightKind.Write : DocumentHighlightKind.Read,
                });
            }
        }

        return Task.FromResult<DocumentHighlightContainer?>(new DocumentHighlightContainer(highlights));
    }
}
