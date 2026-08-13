using CommandLine;
using GSCode.Core;
using GSCode.Core.Instrumentation;
using GSCode.Core.Symbols;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Logging;
using GSCode.Server.Transport;
using GSCode.Workspace.Api;
using GSCode.Workspace.Cache;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// All server logging goes to STDERR: the pipe-transport client surfaces stderr in the
// "GSCode Server" output channel, and stdout must stay clean for the stdio transport.
LoggingLevelSwitch levelSwitch = new(LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
    .CreateLogger();

Log.Information("GSCode {Version} language server starting", ServerVersion());

TransportOptions transportOptions = new();
CommandLine.Parser.Default.ParseArguments<TransportOptions>(args).WithParsed(parsed => transportOptions = parsed);

// Before anything resolves the bundled data: those singletons read whichever game is active when
// they are first requested, and that happens during container construction.
if ( !string.IsNullOrWhiteSpace(transportOptions.Game) )
{
    if ( !GameProfile.Select(transportOptions.Game) )
    {
        // Loudly, because the symptom otherwise is "gscode.game does nothing": the setting reads
        // back exactly as written while the server runs as BO3, and nothing disagrees anywhere.
        Log.Warning(
            "Game {Requested} is not a supported dialect — falling back to bo3. Supported: {Supported}",
            transportOptions.Game,
            string.Join(", ", GameProfile.All.Where(static p => p.Supported).Select(static p => p.ShortName)));
    }

    Log.Information("Game profile: {Game} ({Display})", GameProfile.Active.ShortName, GameProfile.Active.DisplayName);
}

TransportResolver.ResolvedTransport transport = await TransportResolver.ResolveAsync(transportOptions, CancellationToken.None);

ServerSettings settings = new();
PhysicalFileSystem fileSystem = new();
ResolverHolder resolverHolder = new(fileSystem);

// Owns the lifetime of the persistent cache; created in OnStarted, drained on exit. A holder
// rather than a local, so the gscode/clearCache handler can close and delete it on request.
CacheHolder cacheHolder = new();

LanguageServer server = await LanguageServer.From(options =>
{
    options
        .WithInput(transport.Input)
        .WithOutput(transport.Output)
        .WithServices(services =>
        {
            services.AddSingleton(settings);
            services.AddSingleton(levelSwitch);
            services.AddSingleton<IFileSystem>(fileSystem);

            // Shared across the whole session. Keyed by resolved path, so a mod's header and the
            // raw one it shadows stay separate entries.
            services.AddSingleton<InsertCache>();
            services.AddSingleton(resolverHolder);
            services.AddSingleton(cacheHolder);
            services.AddSingleton(NameTable.Shared);

            services.AddSingleton(new TextDocumentSelector(
                [.. GameProfile.Active.ScriptGlobs.Select(glob =>
                    new TextDocumentFilter { Pattern = "**/" + glob })]));

            services.AddSingleton(provider =>
            {
                // Each analyzed file gets an insert provider bound to its own context.
                ResolverHolder holder = provider.GetRequiredService<ResolverHolder>();
                IFileSystem files = provider.GetRequiredService<IFileSystem>();

                // ONE cache for every document: a provider is per file, and a header is read by
                // many, which is the whole reason the cache exists.
                InsertCache inserts = provider.GetRequiredService<InsertCache>();
                return new DocumentStore(
                    path => new ResolverInsertProvider(holder.Current, holder.Current.GetContext(path), files, inserts),
                    provider.GetRequiredService<NameTable>(),
                    inserts);
            });

            services.AddSingleton(provider =>
                new DiagnosticsPublisher(provider.GetRequiredService<ILanguageServerFacade>()));
            services.AddSingleton<WorkspaceDiagnosticsPublisher>();
            services.AddSingleton<DependentDiagnosticsRefresher>();
            services.AddSingleton<ServerStatusNotifier>();

            services.AddSingleton<ScriptDatabase>();
            // Lazy factories, not eager instances: the game (and so which data files to read) is
            // selected after ConfigureServices, so loading is deferred to first resolution to give
            // GameProfile.Active a chance to be the workspace's game rather than the startup default.
            services.AddSingleton(_ => LoadBuiltinApi());
            services.AddSingleton(_ => LoadObjectFields());
            services.AddSingleton(_ => LoadStockScripts());
            services.AddSingleton(provider => new NavigationSupport(
                provider.GetRequiredService<DocumentStore>(),
                provider.GetRequiredService<ScriptDatabase>(),
                provider.GetRequiredService<ResolverHolder>()));
            services.AddSingleton(provider => new CompletionEngine(
                provider.GetRequiredService<ScriptDatabase>(),
                provider.GetRequiredService<BuiltinApiSet>(),
                provider.GetRequiredService<ObjectFields>()));
            services.AddSingleton(provider => new SignatureEngine(
                provider.GetRequiredService<ScriptDatabase>(),
                provider.GetRequiredService<BuiltinApiSet>()));

            services.AddSingleton(provider => new WorkspaceIndexer(
                provider.GetRequiredService<ScriptDatabase>(),
                () => resolverHolder.Current,
                provider.GetRequiredService<IFileSystem>(),
                provider.GetRequiredService<NameTable>(),
                provider.GetRequiredService<InsertCache>()));

            services.AddSingleton(provider => new WatchedFileUpdater(
                provider.GetRequiredService<ScriptDatabase>(),
                provider.GetRequiredService<WorkspaceIndexer>()));
        })
        .AddHandler<TextSyncHandler>()
        .AddHandler<DocumentSymbolHandler>()
        .AddHandler<FoldingRangeHandler>()
        .AddHandler<SelectionRangeHandler>()
        .AddHandler<WorkspaceSymbolHandler>()
        .AddHandler<WatchedFilesHandler>()
        .AddHandler<WorkspaceFoldersHandler>()
        .AddHandler<PlanRenameHandler>()
        .AddHandler<ClearCacheHandler>()
        .AddHandler<BuiltinAtHandler>()
        .AddHandler<HoverHandler>()
        .AddHandler<DefinitionHandler>()
        .AddHandler<ReferencesHandler>()
        .AddHandler<DocumentHighlightHandler>()
        .AddHandler<DocumentLinkHandler>()
        .AddHandler<SemanticTokensHandler>()
        .AddHandler<CompletionHandler>()
        .AddHandler<SignatureHelpHandler>()
        .AddHandler<CodeLensHandler>()
        .AddHandler<RenameHandler>()
        .AddHandler<PrepareRenameHandler>()
        .AddHandler<CallHierarchyHandler>()
        .AddHandler<TypeHierarchyHandler>()
        .AddHandler<InlayHintHandler>()
        .AddHandler<DocumentFormattingHandler>()
        .AddHandler<DocumentRangeFormattingHandler>()
        .AddHandler<DocumentOnTypeFormattingHandler>()
        .AddHandler<CodeActionHandler>()
        .AddHandler<ConfigurationHandler>()
        .OnInitialize((languageServer, request, cancellationToken) =>
        {
            if ( request.InitializationOptions is JToken initializationOptions )
            {
                settings.Apply(initializationOptions);
                levelSwitch.MinimumLevel = ServerLogLevel.FromSetting(settings.ServerLogLevel);

                // A safety net for hosts that do not pass --game: the profile is normally chosen
                // from the command line before the container is built, because the bundled data
                // resolves during construction and would otherwise load for the default game.
                GameProfile.Select(settings.Game);
            }

            List<string> workspaceFolders = [];
            if ( request.WorkspaceFolders is not null )
            {
                foreach ( WorkspaceFolder folder in request.WorkspaceFolders )
                {
                    workspaceFolders.Add(folder.Uri.GetFileSystemPath());
                }
            }
            else if ( request.RootUri is not null )
            {
                workspaceFolders.Add(request.RootUri.GetFileSystemPath());
            }

            // Same builder the workspace-folder handler uses, so a rebuild after a folder
            // change can never drift from what initialize constructed.
            RootConfig rootConfig = WorkspaceFoldersHandler.BuildConfig(settings, workspaceFolders, fileSystem);

            resolverHolder.Current = new PathResolver(rootConfig, fileSystem);

            if ( rootConfig.RawRoot is null )
            {
                // Workspace-only mode is first-class: one info line, never a popup. It names the
                // folder that was searched for, because the commonest reason to find nothing is
                // being in the wrong game mode — share\raw and raw are not interchangeable.
                Log.Information(
                    "No game root for {Game}: nothing configured, and no {Subfolder} folder above "
                    + "the workspace folders. Set gscode.rawPath to the game's raw folder. "
                    + "Resolving against the workspace folders only.",
                    GameProfile.Active.ShortName,
                    GameProfile.Active.RawSubfolder);
            }
            else
            {
                // Whether each root was asked for or worked out: "why is it using THAT raw folder"
                // is otherwise unanswerable from the log alone.
                Log.Information(
                    "Roots: raw={RawRoot} ({RawSource}), mods={ModsRoot} ({ModsSource}), workspace folders={FolderCount}",
                    rootConfig.RawRoot,
                    RootSource(settings.RawPath, rootConfig.RawRoot),
                    rootConfig.ModsRoot,
                    RootSource(settings.ModsPath, rootConfig.ModsRoot),
                    rootConfig.WorkspaceFolders.Length);
            }

            Log.Information("Initialize received from {ClientName}", request.ClientInfo?.Name ?? "unknown client");
            LogEffectiveSettings(settings);
            return Task.CompletedTask;
        })
        .OnInitialized((languageServer, request, response, cancellationToken) =>
        {
            // Declared explicitly rather than left to the protocol default. Every range this
            // server produces comes from SourceText, which indexes UTF-16 code units, so a
            // client negotiating UTF-8 offsets would silently mis-place ranges in any file
            // containing astral characters. UTF-16 is the encoding every LSP client supports.
            response.Capabilities.PositionEncoding = PositionEncodingKind.UTF16;

            Log.Information("GSCode {Version} server initialized", ServerVersion());
            return Task.CompletedTask;
        })
        .OnStarted((languageServer, cancellationToken) =>
        {
            // Which game is actually parsing, sent as soon as the connection can carry it. The
            // status bar shows this permanently, so it must not depend on indexing: with
            // workspaceIndexingMode=off nothing below this line runs, and a label that arrived only
            // with gscode/indexingComplete would never appear for those users at all.
            //
            // It is also the server's own answer rather than the client's gscode.game setting. An
            // unrecognised name falls back to BO3, so the setting says what was asked for while this
            // says what was selected — and a status bar confirming a game that is not in use is
            // worse than none, because it rules out the very thing that is wrong.
            languageServer.SendNotification(
                "gscode/serverReady",
                new ServerReadyParams(GameProfile.Active.Abbreviation, GameProfile.Active.DisplayName));

            // Kick off cold-start indexing only once the server is fully started — the
            // client connection is ready to receive gscode/indexing* notifications now
            // (sending them during OnInitialized drops them). Editor traffic is unaffected.
            IndexingMode mode = settings.WorkspaceIndexingMode.ToLowerInvariant() switch
            {
                "off" => IndexingMode.Off,
                "full" => IndexingMode.Full,
                _ => IndexingMode.Partial,
            };

            if ( mode != IndexingMode.Off )
            {
                WorkspaceIndexer indexer = languageServer.Services.GetRequiredService<WorkspaceIndexer>();
                IndexProgressNotifier notifier = new(languageServer.Services.GetRequiredService<ILanguageServerFacade>());

                // Open the persistent cache and prime the indexer with its restored records.
                if ( settings.EnableWorkspaceCache )
                {
                    try
                    {
                        SqliteCache.CleanUpLegacyCache();
                        RootConfig roots = resolverHolder.Current.Config;
                        List<string> cacheKeyRoots = [.. roots.WorkspaceFolders];
                        if ( roots.RawRoot is not null )
                        {
                            cacheKeyRoots.Add(roots.RawRoot);
                        }

                        if ( roots.ModsRoot is not null )
                        {
                            cacheKeyRoots.Add(roots.ModsRoot);
                        }

                        string databasePath = SqliteCache.ResolveDatabasePath(cacheKeyRoots);
                        string identity = ServerBuildIdentity.Compute(
                            BundledDataFilePaths(), GameProfile.Active.ShortName);
                        SqliteCache workspaceCache = SqliteCache.Open(databasePath, identity);
                        cacheHolder.Set(workspaceCache, databasePath);
                        indexer.UseCache(workspaceCache, workspaceCache.LoadAll());
                    }
                    catch ( Exception exception )
                    {
                        Log.Error(exception, "Failed to open the workspace cache; continuing without it");
                    }
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // The connection's output pump needs a moment before a notification will
                        // survive — sending inside the initialize/initialized window drops them.
                        // The WORK does not need that moment, and used to wait for it anyway: half
                        // a second of an idle process on every single start. The wait now gates the
                        // notifier instead, so indexing runs through it.
                        notifier.SendNothingBefore(Task.Delay(500, CancellationToken.None));

                        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        IndexOutcome outcome = await indexer.IndexAsync(mode, notifier, CancellationToken.None);
                        stopwatch.Stop();
                        Log.Information(
                            "Workspace indexing complete: {Count} files in {Seconds:F1}s ({Restored:N0} from cache)",
                            outcome.Total,
                            stopwatch.Elapsed.TotalSeconds,
                            outcome.Restored);
                        // A dropped cache write is not an error the user can act on, but it is the
                        // difference between the next start being warm and it silently re-analysing
                        // part of the workspace. It used to be invisible.
                        if ( cacheHolder.Current is SqliteCache activeCache && activeCache.DroppedWrites > 0 )
                        {
                            Log.Warning(
                                "{Count} record(s) could not be queued for the workspace cache; those files "
                                + "will be re-analysed on the next start",
                                activeCache.DroppedWrites);
                        }

                        if ( outcome.SkippedOversized > 0 )
                        {
                            Log.Warning(
                                "{Count} file(s) skipped: larger than the {Limit} MB analysis limit",
                                outcome.SkippedOversized,
                                WorkspaceIndexer.MaxAnalysedCharacters / (1024 * 1024));
                        }

                        // Guarded: the breakdown walks every record in both stores plus every GSH,
                        // accumulating counts and a namespace set, to produce ONE Verbose line. That
                        // traversal ran whatever the log level was.
                        if ( Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose) )
                        {
                            LogIndexBreakdown(languageServer.Services.GetRequiredService<ScriptDatabase>());
                        }

                        // Only now: the cross-file picture is complete, and a record's stored
                        // diagnostics are meaningless for files still waiting to be analysed.
                        languageServer.Services.GetRequiredService<WorkspaceDiagnosticsPublisher>().Refresh();

                        // That publisher deliberately skips OPEN documents, so on its own it leaves
                        // the one file the user is actually looking at stale. A tab restored with the
                        // window is opened during initialize, which is before this point, so its
                        // didOpen linted it against a half-built index — and the lints gated on
                        // HasCompletedIndex (5013/5014/5025/5026) stayed silent. The file looked clean
                        // until it was closed and reopened, which is why starting on a DIFFERENT file
                        // and switching to it appeared to fix the problem: that switch was the
                        // didOpen. Same reasoning as an on-disk change: the world moved under every
                        // open document and none of them owns the event, so all of them are
                        // dependents. Costs a lint pass each, not a re-parse.
                        languageServer.Services.GetRequiredService<DependentDiagnosticsRefresher>().Schedule();

                        // Sampled before the monitor starts, so the number reflects the state
                        // indexing left behind rather than anything steady-state traffic did.
                        LogMemoryReport("indexing", outcome);

                        // Let the cache writer finish FIRST. It is handed a record per file and
                        // serializes and gzips each one, so it is still allocating well after
                        // IndexAsync returns — compacting before it drains measures a heap that is
                        // about to be dirtied again, which is precisely the "drops, then climbs
                        // back" the memory report kept showing.
                        if ( cacheHolder.Current is SqliteCache draining )
                        {
                            await draining.WaitForIdleAsync(CancellationToken.None);
                        }

                        Compact();
                        LogMemoryReport("compaction", outcome);

                        // Compiles to nothing without -p:GscodeInstrumentation=true, so a normal
                        // build pays neither the timing scopes nor this dump.
                        PerfTracker.Report(line => Log.Information("Perf  {Scope}", line));

                        // Start sampling memory only now — during indexing it climbs steadily,
                        // and every sample would be a change. One sampler serves both the
                        // status-bar tooltip and the verbose log.
                        _ = languageServer.Services.GetRequiredService<ServerStatusNotifier>()
                            .RunAsync(CancellationToken.None);
                    }
                    catch ( Exception exception )
                    {
                        Log.Error(exception, "Workspace indexing failed");
                    }
                }, CancellationToken.None);
            }

            return Task.CompletedTask;
        });
});

await server.WaitForExit;

// Drain the cache writer so the last records land before we close. A no-op when
// gscode/clearCache already closed it.
await cacheHolder.CloseAsync();

transport.Owner?.Dispose();
Log.Information("GSCode {Version} server exited", ServerVersion());
await Log.CloseAndFlushAsync();

// Logs a formatted breakdown of what the index holds: per-language file counts with a
// raw/mod/workspace split, plus total declared functions, classes, macros, and distinct
// namespaces — the richer signal the old server printed.
static void LogIndexBreakdown(ScriptDatabase database)
{
    int gscRaw = 0;
    int gscMod = 0;
    int gscWorkspace = 0;
    int cscRaw = 0;
    int cscMod = 0;
    int cscWorkspace = 0;
    int functions = 0;
    int classes = 0;
    int macros = 0;
    HashSet<string> namespaces = new(StringComparer.Ordinal);

    foreach ( ScriptRecord record in database.Gsc.AllRecords )
    {
        CategorizeContext(record.ContextId, ref gscRaw, ref gscMod, ref gscWorkspace);
        functions += record.Functions.Length;
        classes += record.Classes.Length;
        macros += record.Macros.Length;
        foreach ( string declared in record.DeclaredNamespaces )
        {
            namespaces.Add(declared);
        }
    }

    foreach ( ScriptRecord record in database.Csc.AllRecords )
    {
        CategorizeContext(record.ContextId, ref cscRaw, ref cscMod, ref cscWorkspace);
        functions += record.Functions.Length;
        classes += record.Classes.Length;
        macros += record.Macros.Length;
        foreach ( string declared in record.DeclaredNamespaces )
        {
            namespaces.Add(declared);
        }
    }

    int gshFiles = 0;
    foreach ( ScriptRecord record in database.AllGshRecords )
    {
        gshFiles++;
        macros += record.Macros.Length;
    }

    System.Text.StringBuilder report = new();
    report.Append("Index contents:");
    report.Append('\n').Append(FormatLanguageLine("GSC", gscRaw, gscMod, gscWorkspace));
    report.Append('\n').Append(FormatLanguageLine("CSC", cscRaw, cscMod, cscWorkspace));
    report.Append('\n').Append($"    GSH  {gshFiles,6:N0} files");
    report.Append('\n').Append("    ─────────────────────────────────────────────");
    report.Append('\n').Append(
        $"    {functions,6:N0} functions · {classes:N0} classes · {macros:N0} macros · {namespaces.Count:N0} namespaces");

    Log.Verbose("{IndexReport}", report.ToString());
}

// Tallies one record's context into the raw / mod / workspace buckets for its language.
static void CategorizeContext(string contextId, ref int raw, ref int mod, ref int workspace)
{
    if ( contextId == "raw" )
    {
        raw++;
    }
    else if ( contextId.StartsWith("mod:", StringComparison.Ordinal) )
    {
        mod++;
    }
    else
    {
        workspace++;
    }
}

// Renders one aligned "GSC  1,234 files  (1,000 raw · 200 mod · 34 workspace)" line, omitting
// any bucket that is empty.
static string FormatLanguageLine(string label, int raw, int mod, int workspace)
{
    int total = raw + mod + workspace;

    List<string> parts = [];
    if ( raw > 0 )
    {
        parts.Add($"{raw:N0} raw");
    }

    if ( mod > 0 )
    {
        parts.Add($"{mod:N0} mod");
    }

    if ( workspace > 0 )
    {
        parts.Add($"{workspace:N0} workspace");
    }

    string split = parts.Count > 0 ? "  (" + string.Join(" · ", parts) + ")" : "";
    return $"    {label}  {total,6:N0} files{split}";
}

// Whether a resolved root is the one the user asked for, or one worked out from the workspace.
// Compares the paths rather than just testing whether the setting is non-empty, so a setting
// naming a folder that is not on disk - which falls back to derivation - reports honestly.
static string RootSource(string configured, string? resolved)
{
    if ( resolved is null )
    {
        return "none";
    }

    if ( configured.Length > 0
        && string.Equals(GSCode.Core.Paths.PathUtil.NormalizeAbsolute(configured), resolved, StringComparison.Ordinal) )
    {
        return "configured";
    }

    return "derived";
}

// Compacts the heap once, at the indexing -> serving transition, when indexing left enough
// fragmentation to be worth it.
//
// Calling GC.Collect is normally wrong, but this is the case that justifies it: a one-off
// phase change after which the allocation profile is completely different. Analysing a file
// allocates token and PToken arrays that clear the 85 KB large-object threshold, so most
// scripts put theirs straight on the LOH — which is NOT compacted by default. Measured on
// 1,105 files: a cold index left 183 MB fragmented out of a 282 MB heap (65% holes) even
// after 19 gen2 collections, because ordinary collections reclaim LOH memory without moving
// anything. Serving requests allocates nothing like that, so the holes would simply persist.
//
// Gated on measured fragmentation so a warm start, which restores records and fragments
// almost nothing, skips the pause entirely.
// One compacting collection at the indexing -> serving transition.
//
// UNCONDITIONAL, and it used to be gated on 32 MB of measured fragmentation. The gate made sense
// while fragmentation was the whole problem — a warm start had none and skipped the pause. It
// stopped making sense once System.GC.ConserveMemory took fragmentation to roughly zero: the gate
// then read "nothing to do" while the large-object heap was still holding tens of megabytes of
// committed, unfragmented, unreturned space that no ordinary collection gives back.
//
// Fragmentation was never the thing worth measuring. What the user sees is committed memory, and
// CompactOnce is the only thing that returns large-object pages to the OS. It runs once per index,
// so its cost is a one-off pause on a thread nobody is waiting on.
static void Compact()
{
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();

    // The second pass collects what the finalizers just released; the LOH mode is one-shot and has
    // already been consumed, so this one is an ordinary compacting gen2.
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}

// A one-shot memory breakdown, logged at the indexing -> serving transition.
//
// The point is the gap between the managed heap and the working set. Cold indexing allocates
// heavily per file (source text, token arrays, AST, extraction builders) at
// ProcessorCount - 1 way parallelism, all of it garbage once the record is built; a warm
// restore just deserializes records. If the LIVE numbers match across a cold and a warm start
// while the working set differs, the extra footprint is grown, uncompacted heap rather than
// retained data — and a one-time compacting collect here is the fix.
// The settings that shape behaviour, logged once at startup at Information — so they are in the
// log a user attaches to a bug report without anyone having to ask for a higher level first.
static void LogEffectiveSettings(ServerSettings settings)
{
    Log.Information("Settings: {Settings}", settings.EffectiveSummary);

    // Only when set: an override is unusual and worth seeing, but a line saying "no override" on
    // every start would be noise.
    if ( settings.RawPath.Length > 0 )
    {
        Log.Information("Setting: rawPath overridden to {Path}", settings.RawPath);
    }

    if ( settings.ModsPath.Length > 0 )
    {
        Log.Information("Setting: modsPath overridden to {Path}", settings.ModsPath);
    }
}

// The detailed memory breakdown, at Verbose.
//
// Gated by the LOG LEVEL rather than an environment variable. A setting the user can change from
// the settings UI beats one that needs an env var and a restart — and GSCODE_INSTRUMENTATION was
// doubly confusing, since PerfTracker already uses that name as a COMPILE-TIME symbol for
// something else entirely.
//
// Called twice a session, after indexing and after compaction, so it is not the noisy part.
static void LogMemoryReport(string phase, IndexOutcome outcome)
{

    GCMemoryInfo info = GC.GetGCMemoryInfo();

    double workingSet = Environment.WorkingSet / (1024.0 * 1024.0);
    double managedLive = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
    double heapSize = info.HeapSizeBytes / (1024.0 * 1024.0);
    double committed = info.TotalCommittedBytes / (1024.0 * 1024.0);
    double fragmented = info.FragmentedBytes / (1024.0 * 1024.0);

    System.Text.StringBuilder report = new();
    report.AppendLine($"Memory after {phase}:");
    report.AppendLine($"    files           {outcome.Total,8:N0}  ({outcome.Restored:N0} restored · {outcome.Analysed:N0} analysed)");
    report.AppendLine($"    working set     {workingSet,8:F1} MB   (what the OS reports)");
    report.AppendLine($"    managed live    {managedLive,8:F1} MB   (retained objects)");
    report.AppendLine($"    heap size       {heapSize,8:F1} MB");
    report.AppendLine($"    committed       {committed,8:F1} MB");
    report.AppendLine($"    fragmented      {fragmented,8:F1} MB");

    // WHERE the fragmentation is, which the total cannot say. This line used to read "(mostly
    // large-object heap)" — a guess that nothing had ever measured, and the two are treated very
    // differently by the collector: gen2 holes are compacted by an ordinary blocking collection,
    // LOH holes are not compacted at all unless LargeObjectHeapCompactionMode asks for it.
    // Knowing which one holds the bulk decides whether the fix is fewer big arrays or fewer
    // long-lived small ones.
    AppendGenerations(report, info);

    report.Append($"    collections     gen0 {GC.CollectionCount(0):N0} · gen1 {GC.CollectionCount(1):N0} · gen2 {GC.CollectionCount(2):N0}");

    Log.Verbose("{MemoryReport}", report.ToString());
}

// Per-generation size and fragmentation, as of each generation's last collection.
//
// The runtime reports five entries in a fixed order — gen0, gen1, gen2, the large-object heap and
// the pinned-object heap — but the count is not contractually five, so the names are indexed
// defensively rather than assumed.
static void AppendGenerations(System.Text.StringBuilder report, GCMemoryInfo info)
{
    string[] names = ["gen0", "gen1", "gen2", "LOH", "POH"];

    ReadOnlySpan<GCGenerationInfo> generations = info.GenerationInfo;
    for ( int index = 0; index < generations.Length; index++ )
    {
        string name = index < names.Length ? names[index] : $"gen{index}";
        double size = generations[index].SizeAfterBytes / (1024.0 * 1024.0);
        double holes = generations[index].FragmentationAfterBytes / (1024.0 * 1024.0);

        report.AppendLine($"      {name,-4}          {holes,8:F1} MB free of {size,8:F1} MB");
    }
}


// Locates the bundled data files whose contents feed the server build identity — the active
// game's set, named by its profile, so a dialect port's data invalidates the cache like BO3's.
// The three data loads, wrapped so each says what it looked for and what it got. GSCode.Workspace
// carries no Serilog reference by design, so the reporting lives here at the call site.
//
// Worth logging loudly: a missing data file is NOT an error to the loaders — it means "this game
// ships none" — so a game whose files failed to deploy behaves exactly like a game that has none,
// and every engine function silently becomes unknown. That failure mode is invisible without this.
static BuiltinApiSet LoadBuiltinApi()
{
    GameProfile game = GameProfile.Active;
    string directory = Path.Combine(AppContext.BaseDirectory, "Api");
    LogDataFile(game, directory, game.ApiFileName(ScriptLanguage.Gsc), "builtin API (gsc)");
    if ( game.HasClientScripts )
    {
        LogDataFile(game, directory, game.ApiFileName(ScriptLanguage.Csc), "builtin API (csc)");
    }

    BuiltinApiSet set = BuiltinApiSet.Load(directory, game);
    Log.Information(
        "Builtin API loaded for {Game}: {Gsc} gsc, {Csc} csc functions",
        game.ShortName, set.For(ScriptLanguage.Gsc).Count, set.For(ScriptLanguage.Csc).Count);

    if ( game.DataFilePrefix is not null && set.For(ScriptLanguage.Gsc).Count == 0 )
    {
        Log.Warning(
            "{Game} declares data prefix '{Prefix}' but its builtin API is EMPTY — every engine "
            + "function will look unknown. Expected {File} in {Directory}.",
            game.ShortName, game.DataFilePrefix, game.ApiFileName(ScriptLanguage.Gsc), directory);
    }

    return set;
}

static ObjectFields LoadObjectFields()
{
    GameProfile game = GameProfile.Active;
    string directory = Path.Combine(AppContext.BaseDirectory, "Api");
    LogDataFile(game, directory, game.ObjectFieldsFileName, "object fields");
    LogDataFile(game, directory, game.RadiantKeysFileName, "radiant keys");

    ObjectFields fields = ObjectFields.Load(directory, game);
    Log.Information(
        "Engine data loaded for {Game}: {Fields} field names, {Keys} radiant keys",
        game.ShortName, fields.FieldNames().Length, fields.RadiantKeysFor(ScriptLanguage.Gsc).Length);

    return fields;
}

static StockScripts LoadStockScripts()
{
    GameProfile game = GameProfile.Active;
    string directory = Path.Combine(AppContext.BaseDirectory, "Api");
    LogDataFile(game, directory, game.StockScriptsFileName, "stock scripts");

    return StockScripts.Load(directory, game);
}

// Reports one data file: what the profile asked for, and whether it is actually there.
static void LogDataFile(GameProfile game, string directory, string? fileName, string what)
{
    if ( fileName is null )
    {
        Log.Debug("{Game} ships no {What} (no data prefix)", game.ShortName, what);
        return;
    }

    string path = Path.Combine(directory, fileName);
    if ( File.Exists(path) )
    {
        Log.Debug("{Game} {What}: {File} ({Bytes} bytes)", game.ShortName, what, fileName, new FileInfo(path).Length);
    }
    else
    {
        Log.Warning("{Game} {What}: {File} NOT FOUND in {Directory}", game.ShortName, what, fileName, directory);
    }
}

static IEnumerable<string> BundledDataFilePaths()
{
    string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
    foreach ( string fileName in GameProfile.Active.BundledDataFileNames )
    {
        yield return Path.Combine(apiDirectory, fileName);
    }
}

// The server's version, as the build stamped it.
//
// Read from the assembly rather than written in the log string, so it cannot drift from what
// actually shipped — the three startup lines used to say "v2" indefinitely while the assembly
// claimed 1.0.0 and the extension said 2.0.0. The single source is <Version> in
// Directory.Build.props, which must match client/package.json since the two ship as one extension.
static string ServerVersion()
{
    System.Reflection.Assembly assembly = typeof(TransportOptions).Assembly;

    // The informational version carries any suffix; the plain AssemblyVersion drops it, so prefer
    // it and fall back only if it is absent.
    string? informational = System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)
        ?.InformationalVersion;

    string version = informational ?? assembly.GetName().Version?.ToString() ?? "unknown";

    // Since .NET 8 the SDK appends "+<full git sha>" to the informational version whenever it builds
    // inside a repository, so this read "2.0.0+95362d3b2dbd71dbb3cf..." - forty hex characters in
    // every startup line. The commit is worth keeping for triage (it identifies the exact build a
    // user is reporting against), so it is shortened to the usual seven rather than switched off.
    int plus = version.IndexOf('+', StringComparison.Ordinal);
    if ( plus >= 0 )
    {
        string revision = version[(plus + 1)..];
        version = revision.Length > 7
            ? string.Concat(version.AsSpan(0, plus + 1), revision.AsSpan(0, 7))
            : version;
    }

    return version;
}
