using GSCode.Core;
using GSCode.Core.Text;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Turns resolved #using/#insert paths into ctrl-clickable links to their target files.</summary>
public sealed class DocumentLinkHandler : DocumentLinkHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly TextDocumentSelector _selector;

    public DocumentLinkHandler(NavigationSupport support, TextDocumentSelector selector)
    {
        _support = support;
        _selector = selector;
    }

    protected override DocumentLinkRegistrationOptions CreateRegistrationOptions(DocumentLinkCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentLinkRegistrationOptions { DocumentSelector = _selector, ResolveProvider = false };
    }

    public override Task<DocumentLinkContainer?> Handle(DocumentLinkParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<DocumentLinkContainer?>(null);
        }

        List<DocumentLink> links = [];

        // #insert edges carry their resolved path directly.
        foreach ( GSCode.Parser.Preprocessing.InsertEdge insert in target.Result.Preprocessed.Inserts )
        {
            if ( insert.ContainingFile is null && insert.ResolvedPath is not null )
            {
                links.Add(new DocumentLink
                {
                    Range = insert.DirectiveRange.ToLsp(),
                    Target = DocumentUri.FromFileSystemPath(insert.ResolvedPath),
                });
            }
        }

        // #using / #include targets resolve through the file's context.
        foreach ( GSCode.Parser.Syntax.Ast.AstNode element in target.Result.Tree.Root.Elements )
        {
            (string Path, TextRange Range)? directive = element switch
            {
                GSCode.Parser.Syntax.Ast.UsingNode usingNode => (usingNode.Path, usingNode.PathRange),
                GSCode.Parser.Syntax.Ast.IncludeNode includeNode => (includeNode.Path, includeNode.PathRange),
                _ => null,
            };

            if ( directive is null )
            {
                continue;
            }

            string extension = target.Language == GSCode.Core.Symbols.ScriptLanguage.Csc
                ? GameProfile.Active.ClientScriptExtension
                : GameProfile.Active.ServerScriptExtension;
            string? resolved = _support.Resolver.Resolve(_support.Resolver.GetContext(target.Path), directive.Value.Path + extension);
            if ( resolved is not null )
            {
                links.Add(new DocumentLink
                {
                    Range = directive.Value.Range.ToLsp(),
                    Target = DocumentUri.FromFileSystemPath(resolved),
                });
            }
        }

        return Task.FromResult<DocumentLinkContainer?>(new DocumentLinkContainer(links));
    }

    // Links are fully resolved up front (ResolveProvider = false), so resolve is a passthrough.
    public override Task<DocumentLink> Handle(DocumentLink request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
