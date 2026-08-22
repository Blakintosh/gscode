using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Folding: declarations/blocks from the AST, comments/doc blocks, dev blocks, and /* region */ pairs.</summary>
public sealed class FoldingRangeHandler : FoldingRangeHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    public FoldingRangeHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
        FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = _selector,
        };
    }

    public override Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGetAnalyzed(
            request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument _, out ParseResult result) )
        {
            return Task.FromResult<Container<FoldingRange>?>(null);
        }

        List<FoldingRange> ranges = [];
        foreach ( FoldingRegion region in FoldingRegions.Compute(result) )
        {
            // Only the kind varies. A declaration or block gets none at all, which the protocol
            // reads as a plain collapsible range rather than one the client can fold by category.
            // The cast is on the first arm rather than the declaration: a switch expression takes
            // its natural type from the arms, and two non-null ones make that non-nullable, which
            // rejects the third before the nullable local can accept it.
            FoldingRangeKind? kind = region.Kind switch
            {
                FoldingRegionKind.Comment => (FoldingRangeKind?)FoldingRangeKind.Comment,
                FoldingRegionKind.UserRegion => FoldingRangeKind.Region,
                _ => null,
            };

            ranges.Add(new FoldingRange
            {
                StartLine = region.StartLine,
                EndLine = region.EndLine,
                Kind = kind,
            });
        }

        return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(ranges));
    }
}
