using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>
/// Document lifecycle: incremental text sync, ~250 ms debounced re-analysis on typing,
/// immediate analysis on open and save, diagnostic clearing on close.
/// </summary>
public sealed class TextSyncHandler : TextDocumentSyncHandlerBase
{
    private const int DebounceMilliseconds = 250;

    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;
    private readonly TextDocumentSelector _selector;

    public TextSyncHandler(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        ScriptDatabase database,
        ResolverHolder resolver,
        TextDocumentSelector selector)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _database = database;
        _resolver = resolver;
        _selector = selector;
    }

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        string extension = Path.GetExtension(uri.GetFileSystemPath()).ToLowerInvariant();
        string languageId = extension switch
        {
            ".csc" => "csc",
            ".gsh" => "gsh",
            _ => "gsc",
        };

        return new TextDocumentAttributes(uri, languageId);
    }

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(
        TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = _selector,
            Change = TextDocumentSyncKind.Incremental,
            Save = new SaveOptions { IncludeText = false },
        };
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        OpenDocument document = _documents.Open(
            request.TextDocument.Uri.GetFileSystemPath(),
            request.TextDocument.Text,
            request.TextDocument.Version ?? 0);

        AnalyzeAndPublish(document, request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        if ( !_documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) )
        {
            return Unit.Task;
        }

        foreach ( TextDocumentContentChangeEvent change in request.ContentChanges )
        {
            _documents.ApplyChange(document, change.Range?.ToCore(), change.Text, request.TextDocument.Version ?? document.Version + 1);
        }

        ScheduleDebouncedAnalysis(document, request.TextDocument.Uri);
        return Unit.Task;
    }

    public override async Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Saves bypass the debounce: dependents and the cache (P5/P6) key off saved state.
        if ( _documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) )
        {
            if ( document.PendingAnalysis is not null )
            {
                await document.PendingAnalysis.CancelAsync();
            }

            AnalyzeAndPublish(document, request.TextDocument.Uri);
        }

        return Unit.Value;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _documents.Close(request.TextDocument.Uri.GetFileSystemPath());
        _diagnostics.Clear(request.TextDocument.Uri);
        return Unit.Task;
    }

    private void ScheduleDebouncedAnalysis(OpenDocument document, DocumentUri uri)
    {
        document.PendingAnalysis?.Cancel();
        CancellationTokenSource pending = new();
        document.PendingAnalysis = pending;

        _ = RunDebouncedAsync(document, uri, pending.Token);
    }

    private async Task RunDebouncedAsync(OpenDocument document, DocumentUri uri, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, cancellationToken);
            AnalyzeAndPublish(document, uri);
        }
        catch ( OperationCanceledException )
        {
            // Superseded by a newer edit — not an error.
        }
        catch ( Exception exception )
        {
            Log.Error(exception, "Analysis failed for {Path}", document.Path);
        }
    }

    private void AnalyzeAndPublish(OpenDocument document, DocumentUri uri)
    {
        ParseResult result = _documents.Analyze(document);
        _diagnostics.Publish(uri, document.Version, WithWorkspaceLints(document, result));
    }

    /// <summary>Merges the parse diagnostics with the cross-file lints (namespace-usage).</summary>
    private ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> WithWorkspaceLints(OpenDocument document, ParseResult result)
    {
        // GSH fragments have no language store of their own and no #using semantics to lint.
        if ( document.Language != ScriptLanguage.Gsc && document.Language != ScriptLanguage.Csc )
        {
            return result.AllDiagnostics;
        }

        LanguageStore store = _database.StoreFor(document.Language);
        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> lints = NamespaceUsageLint.Analyze(
            result, store, document.Language, _resolver.Current, document.Path);

        if ( lints.Length == 0 )
        {
            return result.AllDiagnostics;
        }

        return result.AllDiagnostics.AddRange(lints);
    }
}
