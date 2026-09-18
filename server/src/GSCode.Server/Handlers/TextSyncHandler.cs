using GSCode.Core;
using System.Collections.Immutable;
using GSCode.Core.Paths;
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
    private const int DebounceMilliseconds = 250;

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
    /// One analysis in flight per document, at most — see <see cref="AnalysisGate"/>. The 250 ms
    /// debounce only guards the WAIT between an edit and analysis STARTING; once analysis has
    /// started, nothing stopped a second one starting 250 ms later on a file slow enough to still
    /// be running, each carrying its own token arrays and AST. Keyed by normalized path rather
    /// than kept on <see cref="OpenDocument"/> itself: this is scheduling state private to how
    /// THIS handler drives analysis, not a fact about the document.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, AnalysisGate> _analysisGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Coalesces concurrent analysis requests for one document into: whichever is already
    /// running, plus at most one more queued to run immediately after against whatever text is
    /// current by then. <see cref="OpenDocument.Publish"/>'s version CAS already decides which
    /// analysis's RESULT wins when two somehow still overlap (a request thread's
    /// <see cref="DocumentStore.AnalyzeIfStale"/> can still run concurrently with this), so this
    /// gate is purely about not PAYING for more analyses in flight than the debounce intended,
    /// not about correctness of the published result.
    /// </summary>
    private sealed class AnalysisGate
    {
        private int _running;
        private int _rerunRequested;

        /// <summary>
        /// True when the caller should run now; false when another run is already in flight and
        /// has been told to loop once more instead.
        /// </summary>
        public bool TryStart()
        {
            if ( Interlocked.Exchange(ref _running, 1) == 0 )
            {
                return true;
            }

            Volatile.Write(ref _rerunRequested, 1);
            return false;
        }

        /// <summary>Clears any rerun request and reports whether one had been made.</summary>
        public bool ConsumeRerunRequest()
        {
            return Interlocked.Exchange(ref _rerunRequested, 0) != 0;
        }

        public void Finish()
        {
            Volatile.Write(ref _running, 0);
        }
    }

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

    public override async Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken)
    {
        // Saves bypass the debounce: dependents and the cache (P5/P6) key off saved state.
        if ( _documents.TryGet(request.TextDocument.Uri.GetFileSystemPath(), out OpenDocument document) )
        {
            if ( document.PendingAnalysis is not null )
            {
                await document.PendingAnalysis.CancelAsync();
            }

            AnalyzeAndPublish(document);
            RefreshDependentsOfSavedHeader(document);
            WarnIfProtectedRawFile(document);
        }

        return Unit.Value;
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
        _analysisGates.TryRemove(PathUtil.NormalizeAbsolute(path), out _);

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

        RunAnalysisSingleFlight(document);
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
            await Task.Delay(DebounceMilliseconds, cancellationToken);
        }
        catch ( OperationCanceledException )
        {
            // Superseded by a newer edit — not an error.
            return;
        }

        RunAnalysisSingleFlight(document);
    }

    /// <summary>
    /// Runs analysis for a document, coalescing with whatever is already running for it — see
    /// <see cref="AnalysisGate"/>. If nothing is running, runs (and re-runs, if a request arrived
    /// while it was busy) here and now; otherwise queues the rerun and returns immediately,
    /// leaving the in-flight call to pick it up.
    /// </summary>
    private void RunAnalysisSingleFlight(OpenDocument document)
    {
        AnalysisGate gate = _analysisGates.GetOrAdd(document.Path, static _ => new AnalysisGate());
        if ( !gate.TryStart() )
        {
            return;
        }

        try
        {
            do
            {
                try
                {
                    AnalyzeAndPublish(document);
                }
                catch ( Exception exception )
                {
                    Log.Error(exception, "Analysis failed for {Path}", document.Path);
                }
            }
            while ( gate.ConsumeRerunRequest() );
        }
        finally
        {
            gate.Finish();
        }
    }

    private void AnalyzeAndPublish(OpenDocument document)
    {
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // The WINNING snapshot, not document.Version read afterwards: two analyses of the same
        // document can run concurrently, OpenDocument.Publish's version CAS decides which one's
        // parse actually stands, and a caller stamping diagnostics with the LIVE version would
        // describe even a superseded analysis as being about text the client has already moved
        // past — which defeats the version's whole purpose (see AnalysisSnapshot/AnalyzeSnapshot).
        AnalysisSnapshot snapshot = _documents.AnalyzeSnapshot(document);
        ParseResult result = snapshot.Result;
        ImmutableArray<GSCode.Core.Diagnostics.Diagnostic> diagnostics = _linter.Analyze(document, result);

        _diagnostics.Publish(document.Path, snapshot.Version, diagnostics);
        CommitAndRefreshLenses(document, result);

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
        if ( elapsedMilliseconds >= DebounceMilliseconds )
        {
            Log.Warning(
                "Analysis of {Path} took {Elapsed:F0}ms, at or past the {Debounce}ms debounce — "
                + "sustained editing of this file may overlap several analyses in flight",
                document.Path,
                elapsedMilliseconds,
                DebounceMilliseconds);
        }
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
}
