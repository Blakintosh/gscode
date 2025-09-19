using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using GSCode.Parser;

namespace GSCode.NET.LSP.Handlers;

public class SemanticTokensHandler : SemanticTokensHandlerBase
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<SemanticTokensHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public SemanticTokensHandler(ILanguageServerFacade facade,
        ScriptManager scriptManager,
        ILogger<SemanticTokensHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(SemanticTokensCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = _documentSelector,
            Legend = new SemanticTokensLegend
            {
                TokenModifiers = capability.TokenModifiers,
                TokenTypes = capability.TokenTypes
            },
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = false
        };
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(ITextDocumentIdentifierParams @params, CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }

    protected override async Task Tokenize(SemanticTokensBuilder builder, ITextDocumentIdentifierParams identifier, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Semantic tokens request start");
        Script? script = _scriptManager.GetParsedEditor(identifier.TextDocument);

        if (script is not null)
        {
            await script.PushSemanticTokensAsync(builder, cancellationToken);
        }

        _logger.LogDebug("Semantic tokens finished");
    }
}