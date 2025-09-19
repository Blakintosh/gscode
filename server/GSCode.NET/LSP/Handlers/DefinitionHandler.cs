using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using GSCode.Parser;
using GSCode.Parser.SPA;
using GSCode.NET.Util;

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
        _logger.LogDebug("Definition request start");
        var sw = Stopwatch.StartNew();

        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            sw.Stop();
            _logger.LogDebug("Definition abort (no script) in {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return new LocationOrLocationLinks();
        }

        Location? location = await script.GetDefinitionAsync(request.Position, cancellationToken);
        if (location is not null)
        {
            sw.Stop();
            _logger.LogDebug("Definition local resolved in {ElapsedMs} ms: {Uri}:{Range}", sw.ElapsedMilliseconds, location.Uri, location.Range);
            return new LocationOrLocationLinks(location);
        }

        var qual = await script.GetQualifiedIdentifierAtAsync(request.Position, cancellationToken);
        string? ns = qual?.qualifier;
        string name = qual?.name ?? "";

        if (string.IsNullOrEmpty(name))
        {
            sw.Stop();
            _logger.LogDebug("Definition unresolved (no identifier) in {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return new LocationOrLocationLinks();
        }

        try
        {
            ScriptAnalyserData api = new(script.LanguageId);
            var apiFn = api.GetApiFunction(name);
            if (apiFn is not null)
            {
                sw.Stop();
                _logger.LogDebug("Definition identified builtin API '{Name}' in {ElapsedMs} ms", name, sw.ElapsedMilliseconds);
                return new LocationOrLocationLinks();
            }
        }
        catch
        {
            // ignore API lookup failures
        }

        Location? remote = _scriptManager.FindSymbolLocation(ns, name);
        sw.Stop();
        if (remote is not null)
        {
            _logger.LogDebug("Definition resolved remote in {ElapsedMs} ms: {Uri}:{Range}", sw.ElapsedMilliseconds, remote.Uri, remote.Range);
            return new LocationOrLocationLinks(remote);
        }

        _logger.LogDebug("Definition not found in {ElapsedMs} ms (ns={Ns}, name={Name})", sw.ElapsedMilliseconds, ns, name);
        return new LocationOrLocationLinks();
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = _document_selector
        };
    }
}
