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
using GSCode.Parser;

namespace GSCode.NET.LSP.Handlers;
    
internal class HoverHandler : HoverHandlerBase
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<HoverHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public HoverHandler(ILanguageServerFacade facade, 
        ScriptManager scriptManager, 
        ILogger<HoverHandler> logger, 
        TextDocumentSelector documentSelector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Hover request received, processing...");
        var sw = Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        Hover? result = null;

        if(script is not null)
        {
            result = await script.GetHoverAsync(request.Position, cancellationToken);
        }
        sw.Stop();

        _logger.LogInformation("Hover processed in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, result != null);
        return result;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions()
        {
            DocumentSelector = _documentSelector,
        };
    }
}