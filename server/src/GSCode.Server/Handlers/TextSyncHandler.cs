using GSCode.Core;
using System.Collections.Immutable;
using GSCode.Core.Paths;
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
/// Payload for gscode/gameMismatch: the selected game does not match what the file looks like.
///
/// Carries the roster rather than letting the client keep its own, and carries it HERE rather than
/// making the client ask: the offer to switch is one notification and should not need a round trip
/// to be able to list anything. It is the same list <see cref="GameRoster"/> gives the picker
/// command, so the two offers can never disagree about which games exist.
/// </summary>
public sealed record GameMismatchParams(
    string SelectedGame,
    string SelectedDisplayName,
    bool FileLooksLikeBlackOps3,
    IReadOnlyList<SupportedGame> SupportedGames);

/// <summary>
/// Document lifecycle: incremental text sync, ~250 ms debounced re-analysis on typing,
/// immediate analysis on open and save, diagnostic clearing on close.
/// </summary>
public sealed class TextSyncHandler : TextDocumentSyncHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly DiagnosticsPublisher _diagnostics;
    private readonly ScriptDatabase _database;
    private readonly ResolverHolder _resolver;
    private readonly TextDocumentSelector _selector;
    private readonly ServerSettings _settings;
    private readonly StockScripts _stockScripts;
    private readonly DocumentLinter _linter;
    private readonly ILanguageServerFacade _server;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;
    private readonly DependentDiagnosticsRefresher _dependents;
    private readonly InsertCache _inserts;
    private readonly ConnectionSettleGate _settleGate;

    /// <summary>
    /// Coalesces concurrent analysis requests for one document into one running plus at most one
    /// queued rerun — see <see cref="SingleFlightAnalysis"/>, which owns the per-path gate state
    /// this used to keep directly.
    /// </summary>
    private readonly SingleFlightAnalysis _singleFlight;

    public TextSyncHandler(
        DocumentStore documents,
        DiagnosticsPublisher diagnostics,
        ScriptDatabase database,
        ResolverHolder resolver,
        TextDocumentSelector selector,
        ServerSettings settings,
        StockScripts stockScripts,
        DocumentLinter linter,
        ILanguageServerFacade server,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics,
        DependentDiagnosticsRefresher dependents,
        InsertCache inserts,
        ConnectionSettleGate settleGate)
    {
        _settleGate = settleGate;
        _inserts = inserts;
        _dependents = dependents;
        _workspaceDiagnostics = workspaceDiagnostics;
        _linter = linter;
        _documents = documents;
        _diagnostics = diagnostics;
        _database = database;
        _resolver = resolver;
        _selector = selector;
        _settings = settings;
        _stockScripts = stockScripts;
        _server = server;
        _singleFlight = new SingleFlightAnalysis(documents);
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

        // Take back the workspace publisher's set BEFORE the client's spelling is remembered: it
        // published under the on-disk one, which is what the fallback still resolves to here. Then
        // remember, so everything published from now on addresses the tab the user is looking at.
        // Both halves matter — without the take-back, a file with indexed problems that is then
        // opened carries the index's set AND this handler's until the next workspace refresh.
        _workspaceDiagnostics.OnDocumentOpened(document.Path);
        _diagnostics.Remember(document.Path, request.TextDocument.Uri);

        // Off the handler thread, not inline: a window's worth of restored tabs used to run N
        // full parse-plus-lint passes back to back ON THIS THREAD, one per didOpen, contending
        // with a cold index that is already using every other core for the same work. Nothing
        // here needs to finish before the handler returns — analysis publishes its own
        // diagnostics once it does, exactly like the debounced edit path already does.
        ScheduleImmediateAnalysis(document);
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

        // Once per session; Interlocked so two files opening at once cannot both prompt. Claimed
        // eagerly rather than after the send completes — the send below is itself deferred until
        // the connection has settled, so eager-claim-then-deferred-send is what stops two
        // concurrent opens from both queuing a copy, not what caused the notification to go
        // missing. That was the send itself landing in the drop window, which the settle gate
        // fixes at its source.
        if ( Interlocked.Exchange(ref _gameMismatchNotified, 1) != 0 )
        {
            return;
        }

        _settleGate.SendOnceSettled(() => _server.SendNotification(
            "gscode/gameMismatch",
            new GameMismatchParams(
                active.ShortName,
                active.DisplayName,
                shape == GameShape.BlackOps3,
                GameRoster.Supported())));
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

        ScheduleDebouncedAnalysis(document);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Saves bypass the debounce: dependents and the cache (P5/P6) key off saved state.
        if ( _documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) )
        {
            // Scheduled like every other analysis rather than run right here. Two things were wrong
            // with running it inline: it was a full parse and lint ON THE LSP HANDLER THREAD, the
            // same cost didOpen was moved off; and it went around AnalysisGate, so a save landing
            // while the debounced pass was still running gave one document two concurrent analyses
            // — the exact pile-up the gate exists to prevent. Cancelling the pending analysis is
            // now part of scheduling, and unlike the await it replaces it actually STOPS a run that
            // is already past the debounce.
            ScheduleImmediateAnalysis(document);
            RefreshDependentsOfSavedHeader(document);
            WarnIfProtectedRawFile(document);
        }

        return Unit.Task;
    }

    /// <summary>
    /// Republishes the other open documents when the file just saved is a header.
    ///
    /// A GSH is read from DISK by every file that inserts it, so its edits reach them at the save
    /// and not before — which is why this is on the save path rather than the analysis one, and why
    /// typing in a header costs nothing here. Two things have to happen and neither implies the
    /// other: the cache has to drop the copy it lexed before the save, and the documents whose
    /// parses expanded that copy have to be told, since not one character of THEIR text changed.
    ///
    /// Without it, editing a macro's value and saving left every open dependent showing the old
    /// value on hover until something was typed into it. The export signature does not cover this
    /// case on its own: the header's record was committed from its buffer when the debounce fired,
    /// so by the time the save arrives the signature has already moved and moves no further.
    /// </summary>
    private void RefreshDependentsOfSavedHeader(OpenDocument document)
    {
        if ( document.Language != ScriptLanguage.Gsh )
        {
            return;
        }

        _inserts.Invalidate(document.Path);
        _dependents.Schedule(document.Path);
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
        _diagnostics.Clear(path);
        _diagnostics.Forget(path);
        _singleFlight.Forget(PathUtil.NormalizeAbsolute(path));

        // Clearing is right for what THIS handler published, but the file may still be in the
        // workspace scope, where its problems are supposed to stay visible. Without handing it
        // back, opening and closing a broken file would make it look clean.
        _workspaceDiagnostics.OnDocumentClosed(path);

        return Unit.Task;
    }

    /// <summary>
    /// Schedules a document's first analysis (on open) onto the thread pool rather than running
    /// it on the LSP handler thread — see the call site's comment for why. No delay, unlike
    /// <see cref="ScheduleDebouncedAnalysis"/>: a file opens once, so there is nothing to coalesce,
    /// only work to get off the request path. <c>Task.Delay(0, ...)</c> would NOT do that — a
    /// zero-length delay completes synchronously, running the whole continuation inline on the
    /// caller's thread exactly like today's bug — so this uses <see cref="Task.Run(Action)"/>
    /// instead, which is the one thing here that actually guarantees a different thread.
    /// </summary>
    private void ScheduleImmediateAnalysis(OpenDocument document)
    {
        document.PendingAnalysis?.Cancel();
        CancellationTokenSource pending = new();
        document.PendingAnalysis = pending;

        _ = Task.Run(() => RunImmediate(document, pending.Token), pending.Token);
    }

    private void RunImmediate(OpenDocument document, CancellationToken cancellationToken)
    {
        if ( cancellationToken.IsCancellationRequested )
        {
            // Superseded already — an edit arrived before the thread pool picked this up.
            return;
        }

        _singleFlight.Run(document, cancellationToken, AnalyzeAndPublish);
    }

    private void ScheduleDebouncedAnalysis(OpenDocument document)
    {
        document.PendingAnalysis?.Cancel();
        CancellationTokenSource pending = new();
        document.PendingAnalysis = pending;

        _ = RunDebouncedAsync(document, pending.Token);
    }

    private async Task RunDebouncedAsync(OpenDocument document, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AnalysisTiming.DebounceMilliseconds, cancellationToken);
        }
        catch ( OperationCanceledException )
        {
            // Superseded by a newer edit — not an error.
            return;
        }

        _singleFlight.Run(document, cancellationToken, AnalyzeAndPublish);
    }

    private void AnalyzeAndPublish(OpenDocument document, CancellationToken cancellationToken)
    {
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // The WINNING snapshot, not document.Version read afterwards: two analyses of the same
        // document can run concurrently, OpenDocument.Publish's version CAS decides which one's
        // parse actually stands, and a caller stamping diagnostics with the LIVE version would
        // describe even a superseded analysis as being about text the client has already moved
        // past — which defeats the version's whole purpose (see AnalysisSnapshot/AnalyzeSnapshot).
        AnalysisSnapshot snapshot = _documents.AnalyzeSnapshot(document, cancellationToken);
        ParseResult result = snapshot.Result;
        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics =
            _linter.Analyze(document, result, cancellationToken);

        // Last look before anything leaves this method. A pass superseded during the lint must not
        // publish, and — more importantly — must not COMMIT: a record written from a parse of text
        // the user has already replaced is what every other file's diagnostics are then computed
        // against. Same for a document that stopped being the open one while this ran.
        cancellationToken.ThrowIfCancellationRequested();

        if ( !AnalysisGate.IsStillLive(_documents, document) )
        {
            return;
        }

        _diagnostics.Publish(document.Path, snapshot.Version, diagnostics);
        CommitAndScheduleDependents(document, result);

        double elapsedMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;

        // The single most useful verbose line there is: it says whether the server reacted to a
        // keystroke at all, how long it took, and what it decided — which is most of what anyone
        // turns verbose logging on to find out.
        Log.Verbose(
            "Analysed {Path} v{Version} in {Elapsed:F1}ms → {Count} diagnostic(s)",
            document.Path,
            snapshot.Version,
            elapsedMilliseconds,
            diagnostics.Length);

        // The corpus measures a per-file max well inside the debounce (PERF.md), so this is not
        // expected to fire on ordinary scripts — it exists to turn "we assume every file is fast
        // enough" into evidence from a real workspace where the assumption is wrong, rather than a
        // silent pile-up of overlapping analyses (see AnalysisGate) that nobody gets told about.
        if ( elapsedMilliseconds >= AnalysisTiming.DebounceMilliseconds )
        {
            Log.Warning(
                "Analysis of {Path} took {Elapsed:F0}ms, at or past the {Debounce}ms debounce — "
                + "sustained editing of this file may overlap several analyses in flight",
                document.Path,
                elapsedMilliseconds,
                AnalysisTiming.DebounceMilliseconds);
        }
    }

    /// <summary>
    /// Folds the edited file's symbols back into the database, and schedules the fan-out that
    /// republishes what this edit changed for everyone else.
    ///
    /// Without the commit, the reference index still held whatever the last INDEX pass saw, so
    /// adding or removing a call left "N references" showing the old number until a reindex.
    ///
    /// The code-lens refresh the client needs on top of that is the fan-out's job rather than this
    /// method's, which is why the name says nothing about lenses. It used to be sent from here,
    /// once per analysis, undebounced: typing a function's
    /// name changes the export signature on EVERY keystroke, so a client with lenses on re-requested
    /// them for every visible document about four times a second — and one such request measured
    /// 164 ms on the densest cod4 script (see HandlerCostTests). The refresher already coalesces
    /// exactly this event, over exactly this trigger, and it also covers the on-disk-change case
    /// this method could not see.
    /// </summary>
    private void CommitAndScheduleDependents(OpenDocument document, ParseResult result)
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
    }
}
