using GSCode.Parser;
using GSCode.Parser.Syntax;
using GSCode.Parser.Syntax.Ast;
using GSCode.Server.Mapping;
using GSCode.Workspace.Documents;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Smart expand-selection: the ancestor chain of syntax nodes at each position.</summary>
public sealed class SelectionRangeHandler : SelectionRangeHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly TextDocumentSelector _selector;

    public SelectionRangeHandler(DocumentStore documents, TextDocumentSelector selector)
    {
        _documents = documents;
        _selector = selector;
    }

    protected override SelectionRangeRegistrationOptions CreateRegistrationOptions(
        SelectionRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SelectionRangeRegistrationOptions
        {
            DocumentSelector = _selector,
        };
    }

    public override Task<Container<SelectionRange>?> Handle(SelectionRangeParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGetAnalyzed(
            request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument _, out ParseResult result) )
        {
            return Task.FromResult<Container<SelectionRange>?>(null);
        }

        ScriptNode root = result.Tree.Root;
        List<SelectionRange> results = [];

        foreach ( Position position in request.Positions )
        {
            List<AstNode> chain = AstSearch.ChainAt(root, position.ToCore());

            // Build outermost → innermost, linking each to its parent (init-only property).
            SelectionRange? current = null;
            foreach ( AstNode node in chain )
            {
                if ( current is null )
                {
                    current = new SelectionRange { Range = node.Range.ToLsp() };
                }
                else
                {
                    current = new SelectionRange { Range = node.Range.ToLsp(), Parent = current };
                }
            }

            results.Add(current ?? new SelectionRange { Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(position, position) });
        }

        return Task.FromResult<Container<SelectionRange>?>(new Container<SelectionRange>(results));
    }
}
