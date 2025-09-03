using GSCode.NET.LSP;
using GSCode.Parser;
using GSCode.Parser.SA;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace GSCode.NET.LSP.Handlers;

using SaSymbolKind = GSCode.Parser.SA.SymbolKind;

internal sealed class ReferencesHandler : ReferencesHandlerBase
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<ReferencesHandler> _logger;
    private readonly TextDocumentSelector _selector;

    public ReferencesHandler(ILanguageServerFacade facade, ScriptManager scriptManager, ILogger<ReferencesHandler> logger, TextDocumentSelector selector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _selector = selector;
    }

    public override async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
    {
        Script? script = _scriptManager.GetParsedEditor(request.TextDocument);
        if (script is null)
        {
            return new LocationContainer();
        }

        var qid = await script.GetQualifiedIdentifierAt(request.Position, cancellationToken);
        if (qid is null)
        {
            return new LocationContainer();
        }

        string ns = qid.Value.qualifier ?? (script.DefinitionsTable?.CurrentNamespace ?? "");
        string name = qid.Value.name;

        var keys = new List<SymbolKey>
        {
            new SymbolKey(SaSymbolKind.Function, ns, name),
            new SymbolKey(SaSymbolKind.Class, ns, name)
        };

        List<Location> results = new();
        foreach (var loaded in _scriptManager.GetLoadedScripts())
        {
            Script s = loaded.Script;
            foreach (var key in keys)
            {
                if (s.References.TryGetValue(key, out var ranges))
                {
                    foreach (var r in ranges)
                    {
                        results.Add(new Location { Uri = loaded.Uri.ToUri(), Range = r });
                    }
                }
            }

            if (request.Context?.IncludeDeclaration == true && s.DefinitionsTable is not null)
            {
                foreach (var key in keys)
                {
                    var loc = s.DefinitionsTable.GetFunctionLocation(key.Namespace, key.Name)
                           ?? s.DefinitionsTable.GetClassLocation(key.Namespace, key.Name);
                    if (loc is not null)
                    {
                        results.Add(new Location { Uri = new Uri(loc.Value.FilePath), Range = loc.Value.Range });
                    }
                }
            }
        }

        return new LocationContainer(results);
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(ReferenceCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ReferenceRegistrationOptions
        {
            DocumentSelector = _selector
        };
    }
}
