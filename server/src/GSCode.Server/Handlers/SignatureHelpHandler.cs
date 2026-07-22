using GSCode.Workspace.Completion;
using GSCode.Server.Mapping;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCode.Server.Handlers;

/// <summary>Signature help for script functions, builtins, and call-shaped keywords.</summary>
public sealed class SignatureHelpHandler : SignatureHelpHandlerBase
{
    private readonly NavigationSupport _support;
    private readonly SignatureEngine _engine;
    private readonly TextDocumentSelector _selector;

    public SignatureHelpHandler(NavigationSupport support, SignatureEngine engine, TextDocumentSelector selector)
    {
        _support = support;
        _engine = engine;
        _selector = selector;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = _selector,
            TriggerCharacters = new Container<string>("(", ","),
            RetriggerCharacters = new Container<string>(","),
        };
    }

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        // Fresh: the active argument is derived from the cursor, which moves with every
        // keystroke while analysis is debounced.
        NavigationTarget? target = _support.ResolveFresh(request.TextDocument.Uri);
        if ( target is null )
        {
            return Task.FromResult<SignatureHelp?>(null);
        }

        SignatureResult? signature = _engine.Resolve(target.Result, target.ContextId, request.Position.ToCore());
        if ( signature is null )
        {
            return Task.FromResult<SignatureHelp?>(null);
        }

        List<ParameterInformation> parameters = [];
        foreach ( SignatureParameter parameter in signature.Parameters )
        {
            parameters.Add(new ParameterInformation
            {
                Label = new ParameterInformationLabel(parameter.Label),
                Documentation = parameter.Documentation.Length > 0
                    ? new StringOrMarkupContent(parameter.Documentation)
                    : null,
            });
        }

        SignatureInformation information = new()
        {
            Label = signature.Label,
            Parameters = new Container<ParameterInformation>(parameters),
            Documentation = signature.Documentation.Length > 0
                ? new StringOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = signature.Documentation })
                : null,
        };

        return Task.FromResult<SignatureHelp?>(new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(information),
            ActiveSignature = 0,
            ActiveParameter = signature.ActiveParameter,
        });
    }
}
