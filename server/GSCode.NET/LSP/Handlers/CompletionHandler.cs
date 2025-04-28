using GSCode.Parser;
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

namespace GSCode.NET.LSP.Handlers;

internal class CompletionHandler(ILanguageServerFacade facade,
    ScriptManager scriptManager,
    ILogger<CompletionHandler> logger,
    TextDocumentSelector documentSelector) : CompletionHandlerBase
{
    private readonly ILanguageServerFacade _facade = facade;
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly ILogger<CompletionHandler> _logger = logger;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public override async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Completion request received, processing...");
        CompletionList? result = null;
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);

        if(script is not null)
        {
            result = await script.GetCompletionAsync(request.Position, cancellationToken);
        }

        _logger.LogInformation("Completion request processed. CompletionList being sent: {result}", result);
        return result;
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            TriggerCharacters = new List<string> { "." }
        };
    }
}
