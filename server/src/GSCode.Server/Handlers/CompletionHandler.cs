using System.Collections.Immutable;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Context-aware completion (statement scope, ns::, member fields, directives, paths).</summary>
public sealed class CompletionHandler : CompletionHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly CompletionEngine _engine;
    private readonly ServerSettings _settings;
    private readonly TextDocumentSelector _selector;
    private readonly BuiltinApiSet _builtins;

    public CompletionHandler(
        NavigationSupport support,
        CompletionEngine engine,
        ServerSettings settings,
        TextDocumentSelector selector,
        BuiltinApiSet builtins)
    {
        _support = support;
        _engine = engine;
        _settings = settings;
        _selector = selector;
        _builtins = builtins;
    }

    /// <summary>Maps the client setting; anything unrecognised keeps the default rather than going silent.</summary>
    private static CallPunctuation CallPunctuationFromSetting(string value)
    {
        if ( string.Equals(value, "off", StringComparison.OrdinalIgnoreCase) )
        {
            return CallPunctuation.Off;
        }

        if ( string.Equals(value, "parens", StringComparison.OrdinalIgnoreCase) )
        {
            return CallPunctuation.Parens;
        }

        return CallPunctuation.ParensAndSemicolon;
    }

    /// <summary>Maps the client setting; anything unrecognised keeps the safer owner-scoped default.</summary>
    private static FieldScope FieldScopeFromSetting(string value)
    {
        return string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) ? FieldScope.All : FieldScope.Owner;
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = _selector,
            // The characters that should re-trigger completion (the "feels dead" fix).
            TriggerCharacters = new Container<string>(".", ":", "#", "&", "%", "\\", "/", "\""),
            // Documentation is rendered per highlighted item rather than for the whole list: a
            // statement-scope completion in a real workspace is thousands of entries, and
            // building a doc block for each on every keystroke is the cost this avoids.
            ResolveProvider = true,
        };
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        // Fresh: the cursor position is live, so it only means anything against live text.
        NavigationTarget? target = _support.ResolveFresh(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult(new CompletionList());
        }

        List<CompletionItem> items = [];
        foreach ( CompletionEntry entry in _engine.Complete(
            target.Result,
            target.ContextId,
            request.Position.ToCore(),
            _settings.CompletionLiterals,
            FieldScopeFromSetting(_settings.CompletionFieldScope),
            CallPunctuationFromSetting(_settings.CompletionCallPunctuation)) )
        {
            items.Add(ToItem(entry, request.TextDocument.Uri));
        }

        return Task.FromResult(new CompletionList(items));
    }

    /// <summary>
    /// Fills in the documentation for the one item the user has highlighted, from the identity
    /// stashed in Data at list-build time. Anything unresolvable comes back untouched, which is a
    /// blank doc pane rather than a failure.
    /// </summary>
    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        if ( request.Data is not JObject data )
        {
            return Task.FromResult(request);
        }

        DocumentUri? uri = ReadUri(data);
        if ( uri is null )
        {
            return Task.FromResult(request);
        }

        NavigationTarget? target = _support.Resolve(uri);
        if ( target is null )
        {
            return Task.FromResult(request);
        }

        string? markdown = RenderDocumentation(
            target,
            data.Value<string>("kind") ?? "",
            data.Value<string>("name") ?? "",
            data.Value<string>("ns") ?? "");

        if ( markdown is null )
        {
            return Task.FromResult(request);
        }

        return Task.FromResult(request with
        {
            Documentation = new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
        });
    }

    private static DocumentUri? ReadUri(JObject data)
    {
        string? text = data.Value<string>("uri");
        if ( string.IsNullOrEmpty(text) )
        {
            return null;
        }

        try
        {
            return DocumentUri.Parse(text);
        }
        catch ( Exception )
        {
            return null;
        }
    }

    /// <summary>
    /// The same renderer that feeds hover, so the two surfaces can never describe a symbol
    /// differently. Script functions win over builtins on a name clash, matching resolution.
    /// </summary>
    private string? RenderDocumentation(NavigationTarget target, string kind, string name, string ns)
    {
        if ( name.Length == 0 )
        {
            return null;
        }

        switch ( kind )
        {
            case nameof(CompletionKind.Function):
            {
                ImmutableArray<ResolvedFunction> functions = DatabaseQueries.LookupFunctions(
                    target.Store,
                    target.ContextId,
                    target.Path,
                    ns.Length > 0 ? ns : null,
                    name.ToLowerInvariant(),
                    askingNamespaces: target.Namespaces);

                if ( functions.Length > 0 )
                {
                    return MarkdownDocRenderer.RenderFunction(functions[0].Function);
                }

                BuiltinFunction? builtin = _builtins.For(target.Language).Find(name);
                return builtin is not null ? MarkdownDocRenderer.RenderBuiltin(builtin) : null;
            }
            case nameof(CompletionKind.Class):
            {
                ImmutableArray<ResolvedClass> classes = DatabaseQueries.LookupClasses(
                    target.Store, target.ContextId, ns.Length > 0 ? ns : null, name.ToLowerInvariant());

                return classes.Length > 0 ? MarkdownDocRenderer.RenderClass(classes[0].Class) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Whether the insert text contains an LSP snippet tab stop (<c>$0</c>, <c>$1</c>, …). Sending
    /// a snippet as PlainText would put the literal "$1" in the buffer.
    /// </summary>
    internal static bool HasTabStop(string insertText)
    {
        for ( int index = 0; index + 1 < insertText.Length; index++ )
        {
            if ( insertText[index] == '$' && char.IsAsciiDigit(insertText[index + 1]) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether resolve can say anything more about this kind than the list already does. Keywords
    /// and literals carry their whole documentation up front, so tagging them would just cost a
    /// round trip per highlighted item.
    /// </summary>
    private static bool IsResolvable(CompletionKind kind)
    {
        return kind is CompletionKind.Function or CompletionKind.Class;
    }

    private static CompletionItem ToItem(CompletionEntry entry, DocumentUri uri)
    {
        // Any tab stop, not just $0: directive snippets place the cursor at $1 first and leave
        // $0 for the end, so checking only for $0 would send them as literal text.
        bool isSnippet = HasTabStop(entry.InsertText);
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
            FilterText = entry.FilterText.Length > 0 ? entry.FilterText : null,
            InsertTextFormat = isSnippet ? InsertTextFormat.Snippet : InsertTextFormat.PlainText,
            Command = entry.RetriggerCompletion ? RetriggerCommand : null,
            Data = IsResolvable(entry.Kind) ? ResolveData(entry, uri) : null,
        };
    }

    /// <summary>
    /// Run after the insert, to reopen the suggestion list where the snippet left the cursor.
    ///
    /// Accepting `#precache` lands between the quotes of its first argument, but nothing reopens
    /// the list there — the user had to delete the inserted quotes and retype one just to fire the
    /// '"' trigger character again. This is the editor's own built-in command; a client that does
    /// not have it simply does nothing, which is the behaviour we already had.
    /// </summary>
    private static readonly Command RetriggerCommand = new()
    {
        Name = "editor.action.triggerSuggest",
        Title = "Suggest",
    };

    /// <summary>
    /// The identity resolve needs to find this symbol again. Plain strings only — the same
    /// serializer-casing trap the CodeLens arguments hit, since Data round-trips through the
    /// client untouched.
    /// </summary>
    internal static JObject ResolveData(CompletionEntry entry, DocumentUri uri)
    {
        return new JObject
        {
            ["uri"] = uri.ToString(),
            ["kind"] = entry.Kind.ToString(),

            // The symbol's own name, which is not always the label: an imported function is
            // labelled `ns::name` so the namespace is filterable, and looking THAT up finds nothing.
            ["name"] = entry.ResolveName.Length > 0 ? entry.ResolveName : entry.Label,
            ["ns"] = entry.Namespace,
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
                return CompletionItemKind.Folder;
            case CompletionKind.PathFile:
                return CompletionItemKind.File;
            case CompletionKind.Literal:
                return CompletionItemKind.Text;
            default:
                return CompletionItemKind.Snippet;
        }
    }
}
