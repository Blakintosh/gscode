using MediatR;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using System.Collections.Immutable;

namespace GSCode.NET.LSP.Handlers;

public class TextDocumentSyncHandler : ITextDocumentSyncHandler
{
    private readonly ILanguageServerFacade _facade;
    private readonly ScriptManager _scriptManager;
    private readonly ILogger<TextDocumentSyncHandler> _logger;
    private readonly TextDocumentSelector _documentSelector;

    public TextDocumentSyncHandler(ILanguageServerFacade facade, 
        ScriptManager scriptManager,
        ILogger<TextDocumentSyncHandler> logger,
        TextDocumentSelector documentSelector)
    {
        _facade = facade;
        _scriptManager = scriptManager;
        _logger = logger;
        _documentSelector = documentSelector;
    }

    public TextDocumentSyncKind Change { get; } = TextDocumentSyncKind.Full;

    public TextDocumentChangeRegistrationOptions GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector,
            SyncKind = Change
        };
    }

    public TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new(uri, "gsc");
    }

    public async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document changed");
        var diagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();

        IEnumerable<Diagnostic> results = await _scriptManager.UpdateEditorAsync(request.TextDocument, request.ContentChanges, cancellationToken);

        foreach(Diagnostic result in results)
        {
            diagnostics.Add(result);
        }

        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        {
            Diagnostics = new Container<Diagnostic>(diagnostics.ToArray()),
            Uri = request.TextDocument.Uri,
            Version = request.TextDocument.Version
        });

        return Unit.Value;
    }

    public async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document opened");
        IEnumerable<Diagnostic> resultingDiagnostics = await _scriptManager.AddEditorAsync(request.TextDocument, cancellationToken);
        //var resultingDiagnostics = ImmutableArray<Diagnostic>.Empty.ToBuilder();

        _facade.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
        {
            Diagnostics = resultingDiagnostics.ToArray(),
            Uri = request.TextDocument.Uri,
            Version = request.TextDocument.Version
        });

        return Unit.Value;
    }

    public Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document closed");
        _scriptManager.RemoveEditor(request.TextDocument);

        return Unit.Task;
    }

    public Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Document saved");
        //_scriptManager.UpdateScript(request.TextDocument);

        return Unit.Task;
    }

    TextDocumentOpenRegistrationOptions IRegistration<TextDocumentOpenRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }

    TextDocumentCloseRegistrationOptions IRegistration<TextDocumentCloseRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }

    TextDocumentSaveRegistrationOptions IRegistration<TextDocumentSaveRegistrationOptions, TextSynchronizationCapability>.GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new()
        {
            DocumentSelector = _documentSelector
        };
    }
}