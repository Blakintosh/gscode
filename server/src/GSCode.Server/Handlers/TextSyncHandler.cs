using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Parser;
using GSCode.Server.Configuration;
using GSCode.Server.Mapping;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Serilog;

namespace GSCode.Server.Handlers;

/// <summary>Payload for gscode/rawFolderWriteWarning.</summary>
public sealed record RawFolderWriteWarningParams(string Path, string RelativePath, bool IsStockScript);

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
    private readonly ServerSettings _settings;
    private readonly StockScripts _stockScripts;
    private readonly ILanguageServerFacade _server;

    public TextSyncHandler(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        ScriptDatabase database,
        ResolverHolder resolver,
        TextDocumentSelector selector,
        ServerSettings settings,
        StockScripts stockScripts,
        ILanguageServerFacade server)
    {
        _documents = documents;
        _diagnostics = diagnostics;
        _database = database;
        _resolver = resolver;
        _selector = selector;
        _settings = settings;
        _stockScripts = stockScripts;
        _server = server;
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
            WarnIfProtectedRawFile(document);
        }

        return Unit.Value;
    }

    /// <summary>
    /// Tells the client when a just-saved file lives in the game's raw folder, so it can offer
    /// the "you probably meant to edit a mod copy" warning. Nothing is blocked — the save has
    /// already happened; this is purely advisory.
    /// </summary>
    private void WarnIfProtectedRawFile(OpenDocument document)
    {
        RawFileWarningMode mode = RawWriteGuard.ParseMode(_settings.RawFileWarningMode);
        if ( mode == RawFileWarningMode.Off )
        {
            return;
        }

        PathResolver resolver = _resolver.Current;
        ResolutionContext context = resolver.GetContext(document.Path);
        string relativePath = resolver.GetScriptRelativePath(document.Path, context);

        if ( !RawWriteGuard.ShouldWarn(mode, context, relativePath, _stockScripts) )
        {
            return;
        }

        bool isStock = _stockScripts.Contains(relativePath);
        _server.SendNotification(
            "gscode/rawFolderWriteWarning", new RawFolderWriteWarningParams(document.Path, relativePath, isStock));
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

    /// <summary>Merges the parse diagnostics with the cross-file lints (namespace-usage, unused #using).</summary>
    private ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> WithWorkspaceLints(OpenDocument document, ParseResult result)
    {
        // GSH fragments have no language store of their own and no #using semantics to lint.
        if ( document.Language != ScriptLanguage.Gsc && document.Language != ScriptLanguage.Csc )
        {
            return result.AllDiagnostics;
        }

        LanguageStore store = _database.StoreFor(document.Language);
        PathResolver resolver = _resolver.Current;

        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> namespaceLints = NamespaceUsageLint.Analyze(
            result, store, document.Language, resolver, document.Path);
        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> unusedUsings = UnusedUsingLint.Analyze(
            result, store, document.Language, resolver, document.Path);

        if ( namespaceLints.Length == 0 && unusedUsings.Length == 0 )
        {
            return result.AllDiagnostics;
        }

        return result.AllDiagnostics.AddRange(namespaceLints).AddRange(unusedUsings);
    }
}
