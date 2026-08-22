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
            // Every LOCAL lands here: the reference index is keyed by SymbolKey and shared
            // workspace-wide, so locals are deliberately absent from it and are walked from the AST
            // per function instead. Same fallthrough DefinitionHandler and ReferencesHandler take.
            return Task.FromResult(LocalHighlightsAt(target, request.Position.ToCore()));
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

    /// <summary>
    /// The local under the cursor, highlighted across its function. Writes are marked as such —
    /// an assignment, a loop binding, a <c>waittill</c> output and the parameter itself all place
    /// a value in the name, and the editor colours those differently from a read.
    /// </summary>
    private DocumentHighlightContainer? LocalHighlightsAt(
        NavigationTarget target, GSCode.Core.Text.Position position)
    {
        List<DocumentHighlight> highlights = [];
        foreach ( LocalOccurrence occurrence in _support.LocalOccurrencesAt(target, position) )
        {
            highlights.Add(new DocumentHighlight
            {
                Range = occurrence.Range.ToLsp(),
                Kind = occurrence.IsWrite ? DocumentHighlightKind.Write : DocumentHighlightKind.Read,
            });
        }

        if ( highlights.Count == 0 )
        {
            return null;
        }

        return new DocumentHighlightContainer(highlights);
    }
}
