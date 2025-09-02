using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Threading;
using System.Threading.Tasks;
using GSCode.Parser;
using GSCode.Parser.SPA;

namespace GSCode.NET.LSP.Handlers;

internal class DefinitionHandler : DefinitionHandlerBase
{
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<DefinitionHandler> _logger;
    private readonly TextDocumentSelector _document_selector;

    public DefinitionHandler(ScriptManager scriptManager,
        ILogger<DefinitionHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _scriptManager = scriptManager;
        _logger = logger;
        _document_selector = documentSelector;
    }

    public override async Task<LocationOrLocationLinks> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Definition request received, processing...");

        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            return new LocationOrLocationLinks();
        }

        // Try local script lookup first
        Location? location = await script.GetDefinitionAsync(request.Position, cancellationToken);
        if (location is not null)
        {
            _logger.LogInformation("Definition request resolved locally: {uri}:{range}", location.Uri, location.Range);
            return new LocationOrLocationLinks(location);
        }

        // If not found locally, get the qualified identifier using published API
        var qual = await script.GetQualifiedIdentifierAt(request.Position, cancellationToken);
        string? ns = qual?.qualifier;
        string name = qual?.name ?? "";

        if (string.IsNullOrEmpty(name))
            return new LocationOrLocationLinks();

        // If it's a builtin API function, do not return a file location
        try
        {
            ScriptAnalyserData api = new(script.LanguageId);
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null)
            {
                _logger.LogInformation("Definition request: {name} is a builtin API function, no location", name);
                return new LocationOrLocationLinks();
            }
        }
        catch
        {
            // ignore API lookup failures
        }

        Location? remote = _scriptManager.FindSymbolLocation(ns, name);
        if (remote is not null)
        {
            _logger.LogInformation("Definition request resolved remotely: {uri}:{range}", remote.Uri, remote.Range);
            return new LocationOrLocationLinks(remote);
        }

        _logger.LogInformation("Definition request: no location found for {name}", name);
        return new LocationOrLocationLinks();
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions()
        {
            DocumentSelector = _document_selector
        };
    }
}
