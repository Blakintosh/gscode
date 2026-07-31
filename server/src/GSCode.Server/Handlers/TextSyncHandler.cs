using GSCode.Core;
using System.Collections.Immutable;
using GSCode.Core.Symbols;
using GSCode.Workspace.Analysis;
using GSCode.Workspace.Api;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Resolution;
using GSCode.Workspace.Typing;
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

/// <summary>Payload for gscode/gameMismatch: the selected game does not match what the file looks like.</summary>
public sealed record GameMismatchParams(string SelectedGame, string SelectedDisplayName, bool FileLooksLikeBlackOps3);

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
    private readonly BuiltinApiSet _builtins;
    private readonly ObjectFields _objectFields;
    private readonly ILanguageServerFacade _server;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;
    private readonly DependentDiagnosticsRefresher _dependents;

    public TextSyncHandler(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        ScriptDatabase database,
        ResolverHolder resolver,
        TextDocumentSelector selector,
        ServerSettings settings,
        StockScripts stockScripts,
        BuiltinApiSet builtins,
        ObjectFields objectFields,
        ILanguageServerFacade server,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        DependentDiagnosticsRefresher dependents)
    {
        _dependents = dependents;
        _workspaceDiagnostics = workspaceDiagnostics;
        _builtins = builtins;
        _objectFields = objectFields;
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
        string extension = Path.GetExtension(uri.GetFileSystemPath());
        // The language id is the profile's extension for this world, without the dot.
        ScriptLanguage language = GameProfile.Active.LanguageFromExtension(extension);
        string languageId = GameProfile.Active.ExtensionFor(language).TrimStart('.').ToLowerInvariant();

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
        WarnIfGameLooksWrong(document);
        return Unit.Task;
    }

    private int _gameMismatchNotified;

    /// <summary>
    /// Offers to switch the game version when an opened file plainly does not match the selected
    /// one. Fired at most once per session, and only on a decisive import-directive signal — a
    /// wrong guess is just a dismissable prompt, so being quiet matters more than being exhaustive.
    /// </summary>
    private void WarnIfGameLooksWrong(OpenDocument document)
    {
        GameProfile active = GameProfile.Active;
        GameShape shape = GameShapeDetector.Detect(document.Text.Text);
        if ( !GameShapeDetector.Mismatches(active, shape) )
        {
            return;
        }

        // Once per session; Interlocked so two files opening at once cannot both prompt.
        if ( Interlocked.Exchange(ref _gameMismatchNotified, 1) != 0 )
        {
            return;
        }

        _server.SendNotification(
            "gscode/gameMismatch",
            new GameMismatchParams(active.ShortName, active.DisplayName, shape == GameShape.BlackOps3));
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
        string path = request.TextDocument.Uri.GetFileSystemPath();

        _documents.Close(path);
        _diagnostics.Clear(request.TextDocument.Uri);

        // Clearing is right for what THIS handler published, but the file may still be in the
        // workspace scope, where its problems are supposed to stay visible. Without handing it
        // back, opening and closing a broken file would make it look clean.
        _workspaceDiagnostics.OnDocumentClosed(path);

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
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        ParseResult result = _documents.Analyze(document);
        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics = WithWorkspaceLints(document, result);

        _diagnostics.Publish(uri, document.Version, diagnostics);
        CommitAndRefreshLenses(document, result);

        // The single most useful verbose line there is: it says whether the server reacted to a
        // keystroke at all, how long it took, and what it decided — which is most of what anyone
        // turns verbose logging on to find out.
        Log.Verbose(
            "Analysed {Path} v{Version} in {Elapsed:F1}ms → {Count} diagnostic(s)",
            document.Path,
            document.Version,
            System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds,
            diagnostics.Length);
    }

    /// <summary>
    /// Folds the edited file's symbols back into the database and asks the client to re-request
    /// code lenses.
    ///
    /// Without the commit, the reference index still held whatever the last INDEX pass saw, so
    /// adding or removing a call left "N references" showing the old number until a reindex. The
    /// refresh is needed on top: a lens count depends on every file that references the symbol,
    /// which the client has no way to know changed, so editing file A never re-requested the
    /// lenses shown in file B.
    /// </summary>
    private void CommitAndRefreshLenses(OpenDocument document, ParseResult result)
    {
        ResolutionContext context = _resolver.Current.GetContext(document.Path);

        // Read BEFORE the commit replaces it. A file first opened has no prior record, and its
        // exports are new to the world, so treat that as a change too.
        ulong exportsBefore = _database.TryGetAnyRecord(document.Path, out ScriptRecord previous)
            ? ExportSignature.Of(previous)
            : 0;

        ScriptRecord committed = _database.Commit(
            result, context, isDirty: true, _resolver.Current.GetScriptRelativePath(document.Path, context));

        // Other open files' diagnostics are computed against this one, and nothing else republishes
        // them. Only when something they can actually SEE moved — an ordinary keystroke inside a
        // function body leaves the signature alone, which is what keeps this off the edit path.
        if ( ExportSignature.Of(committed) != exportsBefore )
        {
            _dependents.Schedule(document.Path);
        }

        if ( !_settings.CodeLensEnabled )
        {
            return;
        }

        // Fire-and-forget: a failed refresh is cosmetic, and this runs on the analysis path.
        _ = _server.SendRequest("workspace/codeLens/refresh")
            .ReturningVoid(CancellationToken.None)
            .ContinueWith(static _ => { }, TaskScheduler.Default);
    }

    /// <summary>
    /// The file's diagnostics plus the cross-file lints, from the shared pipeline so the editor
    /// and any offline sweep report the same thing.
    /// </summary>
    private ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> WithWorkspaceLints(OpenDocument document, ParseResult result)
    {
        return WorkspaceLints.Analyze(
            result, document.Language, document.Path, _database, _resolver.Current, _builtins, _objectFields);
    }
}
