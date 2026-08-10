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

    /// <summary>
    /// Whether the client renders <c>CompletionItem.labelDetails</c> — the dimmed text beside a
    /// label, which is where a parameter list belongs.
    ///
    /// Captured at registration because it decides how every item is built. A client that does not
    /// advertise it would show nothing at all for a field it ignores, so the parameters are folded
    /// back into the label there instead: degraded, but not silently missing.
    /// </summary>
    private bool _labelDetailsSupported;

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        _labelDetailsSupported = capability.CompletionItem?.LabelDetailsSupport ?? false;

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

    /// <summary>
    /// Answers one completion request.
    ///
    /// The cancellation checks are here because this is a read path with no debounce in front of it
    /// — as are <see cref="SignatureHelpHandler"/> and <see cref="WorkspaceSymbolHandler"/>, which
    /// check for the same reason. Diagnostics wait ~250 ms behind <c>TextSyncHandler</c>, so a burst
    /// of keystrokes produces one analysis; completion answers the keystroke that asked, and a
    /// client that types through its own request cancels it and sends another. Without a check,
    /// every superseded request was still built in full and its result thrown away by the client.
    ///
    /// Two places, for two different costs. The first is the request that was cancelled before it
    /// was ever started, which is the common one under fast typing and costs nothing to skip. The
    /// second is the mapping loop, which is the part worth interrupting: statement scope returns a
    /// median of 1,168 entries and up to 5,059 on the BO3 corpus, and <see cref="ToItem"/> allocates
    /// per entry.
    ///
    /// <c>Complete</c> itself is deliberately NOT made cancellable. That would mean threading a token
    /// through the engine, and the completion sweep in PERF.md puts its p99 at 4.22 ms — there is no
    /// worthwhile interruption point inside it.
    /// </summary>
    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
            CallPunctuationFromSetting(_settings.CompletionCallPunctuation),
            profile: null,
            parameterHints: _settings.CompletionParameterHints) )
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            data.Value<string>("ns") ?? "",
            string.Equals(data.Value<string>("builtin"), "true", StringComparison.Ordinal));

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
    /// differently. Script functions win over builtins on a name clash, matching resolution — EXCEPT
    /// where the row says outright that it is the builtin.
    ///
    /// That exception is the whole point of <paramref name="isBuiltin"/>. Resolution's tie-break is
    /// the right answer to "what does this name mean here", and the wrong answer to "what is this row
    /// in the list", because both rows are offered and only one of them is the engine's. BO3 ships an
    /// engine <c>SpawnSpectator</c> and three scripts declaring one, so the builtin row rendered
    /// <c>globallogic_spawn::spawnSpectator</c> beneath a header reading "builtin".
    /// </summary>
    private string? RenderDocumentation(NavigationTarget target, string kind, string name, string ns, bool isBuiltin)
    {
        if ( name.Length == 0 )
        {
            return null;
        }

        switch ( kind )
        {
            case nameof(CompletionKind.Function):
            {
                if ( isBuiltin )
                {
                    BuiltinFunction? engine = _builtins.For(target.Language).Find(name);
                    return engine is not null ? MarkdownDocRenderer.RenderBuiltin(engine) : null;
                }

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

    /// <summary>How an entry's label is presented, once the client's capabilities are known.</summary>
    internal readonly record struct LabelParts(string Label, string? Detail, string? FilterText);

    /// <summary>
    /// Splits an entry into the label and the dimmed text beside it, according to whether the client
    /// renders <c>labelDetails</c>.
    ///
    /// Supported is the good case and needs no compensation: the label stays the plain name, so
    /// filtering, sorting and resolve all key off it as they should.
    ///
    /// Unsupported means the field would simply be dropped, so the same text is folded into the
    /// label to keep it visible — and THAT is what needs FilterText pinned back to the name. The
    /// editor matches what you type against the label, so a folded-in signature without it would
    /// match parameter names, and typing "team" would surface every function taking one. Only when
    /// the entry has not already set FilterText for its own reasons: a directive filters on its
    /// name without the '#', and an imported function on its qualifier, and neither wants
    /// overwriting here.
    /// </summary>
    internal static LabelParts SplitLabel(CompletionEntry entry, bool labelDetailsSupported)
    {
        string? filterText = entry.FilterText.Length > 0 ? entry.FilterText : null;

        if ( entry.LabelDetail.Length == 0 )
        {
            return new LabelParts(entry.Label, null, filterText);
        }

        if ( labelDetailsSupported )
        {
            return new LabelParts(entry.Label, entry.LabelDetail, filterText);
        }

        return new LabelParts(entry.Label + entry.LabelDetail, null, filterText ?? entry.Label);
    }

    private CompletionItem ToItem(CompletionEntry entry, DocumentUri uri)
    {
        // Any tab stop, not just $0: directive snippets place the cursor at $1 first and leave
        // $0 for the end, so checking only for $0 would send them as literal text.
        bool isSnippet = HasTabStop(entry.InsertText);
        string insertText = entry.InsertText.Length > 0 ? entry.InsertText : entry.Label;

        LabelParts label = SplitLabel(entry, _labelDetailsSupported);

        return new CompletionItem
        {
            Label = label.Label,
            LabelDetails = label.Detail is null ? null : new CompletionItemLabelDetails { Detail = label.Detail },
            Kind = MapKind(entry.Kind),
            Detail = entry.Detail.Length > 0 ? entry.Detail : null,
            Documentation = entry.Documentation.Length > 0
                ? new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = entry.Documentation })
                : null,
            InsertText = insertText,
            FilterText = label.FilterText,
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

            // Which of two same-named rows this is. A name can be an engine function AND a script
            // one, and without this the resolve step re-derives it from the name alone and gets the
            // wrong answer for one of the two rows every time.
            //
            // A STRING, like everything else here: Data round-trips through the client untouched, so
            // a bool would be one more thing with a serializer opinion about shape.
            ["builtin"] = entry.IsBuiltin ? "true" : "false",
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
