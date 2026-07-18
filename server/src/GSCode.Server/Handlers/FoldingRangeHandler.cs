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
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document)
            || document.LatestResult is null )
        {
            return Task.FromResult<Container<FoldingRange>?>(null);
        }

        List<FoldingRange> ranges = [];
        foreach ( FoldingRegion region in FoldingRegions.Compute(document.LatestResult) )
        {
            FoldingRange folding = region.Kind switch
            {
                FoldingRegionKind.Comment => new FoldingRange
                {
                    StartLine = region.StartLine,
                    EndLine = region.EndLine,
                    Kind = FoldingRangeKind.Comment,
                },
                FoldingRegionKind.UserRegion => new FoldingRange
                {
                    StartLine = region.StartLine,
                    EndLine = region.EndLine,
                    Kind = FoldingRangeKind.Region,
                },
                _ => new FoldingRange
                {
                    StartLine = region.StartLine,
                    EndLine = region.EndLine,
                },
            };

            ranges.Add(folding);
        }

        return Task.FromResult<Container<FoldingRange>?>(new Container<FoldingRange>(ranges));
    }
}
