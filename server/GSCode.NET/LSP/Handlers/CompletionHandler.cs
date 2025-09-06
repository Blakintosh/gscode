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
using System.Diagnostics;

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
        var sw = Stopwatch.StartNew();
        CompletionList? result = null;
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);

        if (script is not null)
        {
            result = await script.GetCompletionAsync(request.Position, cancellationToken);
        }
        sw.Stop();

        int count = result is null ? 0 : result.Count();
        _logger.LogInformation("Completion processed in {ElapsedMs} ms. Items: {Count}", sw.ElapsedMilliseconds, count);
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
