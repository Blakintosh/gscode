using GSCode.Workspace.Completion;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Context-aware completion (statement scope, ns::, member fields, directives, paths).</summary>
public sealed class CompletionHandler : CompletionHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly CompletionEngine _engine;
    private readonly TextDocumentSelector _selector;

    public CompletionHandler(NavigationSupport support, CompletionEngine engine, TextDocumentSelector selector)
    {
        _support = support;
        _engine = engine;
        _selector = selector;
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = _selector,
            // The characters that should re-trigger completion (the "feels dead" fix).
            TriggerCharacters = new Container<string>(".", ":", "#", "&", "%", "\\", "/"),
            ResolveProvider = false,
        };
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        NavigationTarget? target = _support.Resolve(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult(new CompletionList());
        }

        List<CompletionItem> items = [];
        foreach ( CompletionEntry entry in _engine.Complete(target.Result, target.ContextId, request.Position.ToCore()) )
        {
            items.Add(ToItem(entry));
        }

        return Task.FromResult(new CompletionList(items));
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // ResolveProvider is false; items are complete already.
        return Task.FromResult(request);
    }

    private static CompletionItem ToItem(CompletionEntry entry)
    {
        bool isSnippet = entry.InsertText.Contains("$0", StringComparison.Ordinal);
        string insertText = entry.InsertText.Length > 0 ? entry.InsertText : entry.Label;

        return new CompletionItem
        {
            Label = entry.Label,
            Kind = MapKind(entry.Kind),
            Detail = entry.Detail.Length > 0 ? entry.Detail : null,
            Documentation = entry.Documentation.Length > 0
                ? new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = entry.Documentation })
                : null,
            InsertText = insertText,
            InsertTextFormat = isSnippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
        };
    }

    private static CompletionItemKind MapKind(CompletionKind kind)
    {
        switch ( kind )
        {
            case CompletionKind.Function:
                return CompletionItemKind.Function;
            case CompletionKind.Class:
                return CompletionItemKind.Class;
            case CompletionKind.Keyword:
                return CompletionItemKind.Keyword;
            case CompletionKind.Variable:
                return CompletionItemKind.Variable;
            case CompletionKind.Field:
                return CompletionItemKind.Field;
            case CompletionKind.Macro:
                return CompletionItemKind.Constant;
            case CompletionKind.Namespace:
                return CompletionItemKind.Module;
            case CompletionKind.AssetType:
                return CompletionItemKind.EnumMember;
            case CompletionKind.PathSegment:
                return CompletionItemKind.File;
            default:
                return CompletionItemKind.Snippet;
        }
    }
}
