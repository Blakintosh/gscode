using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using GSCode.Parser;

namespace GSCode.NET.LSP.Handlers;

internal class SignatureHelpHandler : SignatureHelpHandlerBase
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<SignatureHelpHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public SignatureHelpHandler(ILanguageServerFacade facade,
        ScriptManager scriptManager,
        ILogger<SignatureHelpHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    public override async Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("SignatureHelp request received, processing...");
        var sw = Stopwatch.StartNew();
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            _logger.LogInformation("SignatureHelp finished in {ElapsedMs} ms: no script", sw.ElapsedMilliseconds);
            return null;
        }
        var help = await script.GetSignatureHelpAsync(request.Position, cancellationToken);
        sw.Stop();
        _logger.LogInformation("SignatureHelp finished in {ElapsedMs} ms. Has result: {Has}", sw.ElapsedMilliseconds, help != null);
        return help;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = _documentSelector,
            TriggerCharacters = new Container<string>("(", ",", ")"),
            RetriggerCharacters = new Container<string>(",", ")")
        };
    }
}
