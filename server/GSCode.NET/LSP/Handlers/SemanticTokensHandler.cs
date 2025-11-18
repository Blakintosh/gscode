using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities)
    {
        var tokenModifiers = capability?.TokenModifiers ?? new Container<SemanticTokenModifier>();
        var tokenTypes = capability?.TokenTypes;

        if (tokenTypes == null || !tokenTypes.Any())
        {
            tokenTypes = new Container<SemanticTokenType>(
                SemanticTokenType.Namespace,
                SemanticTokenType.Type,
                SemanticTokenType.Class,
                SemanticTokenType.Enum,
                SemanticTokenType.Interface,
                SemanticTokenType.Struct,
                SemanticTokenType.TypeParameter,
                SemanticTokenType.Parameter,
                SemanticTokenType.Variable,
                SemanticTokenType.Property,
                SemanticTokenType.EnumMember,
                SemanticTokenType.Event,
                SemanticTokenType.Function,
                SemanticTokenType.Method,
                SemanticTokenType.Macro,
                SemanticTokenType.Keyword,
                SemanticTokenType.Modifier,
                SemanticTokenType.Comment,
                SemanticTokenType.String,
                SemanticTokenType.Number,
                SemanticTokenType.Regexp,
                SemanticTokenType.Operator
            );
        }

        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = _documentSelector,
            Legend = new SemanticTokensLegend
            {
                TokenModifiers = tokenModifiers,
                TokenTypes = tokenTypes
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
        _logger.LogInformation("Tokenization request received, processing...");
        Script? script = _scriptManager.GetParsedEditor(identifier.TextDocument);

        if (script is not null)
        {
            await script.PushSemanticTokensAsync(builder, cancellationToken);
        }

        _logger.LogInformation("Tokenization is complete!");
    }
}