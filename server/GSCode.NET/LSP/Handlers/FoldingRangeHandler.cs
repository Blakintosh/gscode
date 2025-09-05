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

internal class FoldingRangeHandler(ILanguageServerFacade facade,
    ScriptManager scriptManager,
    ILogger<FoldingRangeHandler> logger,
    TextDocumentSelector documentSelector) : FoldingRangeHandlerBase
{
    private readonly ILanguageServerFacade _facade = facade;
    private readonly ScriptManager _scriptManager = scriptManager;
    private readonly ILogger<FoldingRangeHandler> _logger = logger;
    private readonly TextDocumentSelector _documentSelector = documentSelector;

    public override Task<Container<FoldingRange>?> Handle(
        FoldingRangeRequestParam request,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<Container<FoldingRange>?>(
            new Container<FoldingRange>(
                new FoldingRange {
                    StartLine = 10,
                    EndLine = 20,
                    Kind = FoldingRangeKind.Region,
                    EndCharacter = 0,
                    StartCharacter = 0
                }
            )
        );

    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions {
            DocumentSelector = _documentSelector
        };
    }
}
