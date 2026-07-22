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

Log.Information("GSCode v2 language server starting");

TransportOptions transportOptions = new();
CommandLine.Parser.Default.ParseArguments<TransportOptions>(args).WithParsed(parsed => transportOptions = parsed);

TransportResolver.ResolvedTransport transport = await TransportResolver.ResolveAsync(transportOptions, CancellationToken.None);
Log.Information("Transport connected (pipe={Pipe}, socket={Socket}, stdio fallback otherwise)", transportOptions.PipeName, transportOptions.SocketPort);

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
            services.AddSingleton(resolverHolder);
            services.AddSingleton(cacheHolder);
            services.AddSingleton(NameTable.Shared);

            services.AddSingleton(new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.gsc" },
                new TextDocumentFilter { Pattern = "**/*.csc" },
                new TextDocumentFilter { Pattern = "**/*.gsh" }));

            services.AddSingleton(provider =>
            {
                // Each analyzed file gets an insert provider bound to its own context.
                ResolverHolder holder = provider.GetRequiredService<ResolverHolder>();
                IFileSystem files = provider.GetRequiredService<IFileSystem>();
                return new DocumentStore(
                    path => new ResolverInsertProvider(holder.Current, holder.Current.GetContext(path), files),
                    provider.GetRequiredService<NameTable>());
            });

            services.AddSingleton(provider =>
                new DiagnosticsPublisher(provider.GetRequiredService<ILanguageServerFacade>()));
            services.AddSingleton<WorkspaceDiagnosticsPublisher>();
            services.AddSingleton<ServerStatusNotifier>();

            services.AddSingleton<ScriptDatabase>();
            services.AddSingleton(BuiltinApiSet.Load(Path.Combine(AppContext.BaseDirectory, "Api")));
            services.AddSingleton(ObjectFields.Load(Path.Combine(AppContext.BaseDirectory, "Api")));
            services.AddSingleton(StockScripts.Load(Path.Combine(AppContext.BaseDirectory, "Api")));
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
                provider.GetRequiredService<NameTable>()));

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
                // Workspace-only mode is first-class: one info line, never a popup.
                Log.Information("Raw root unavailable (raw disabled or TA_TOOLS_PATH not set) — workspace-only resolution");
            }
            else
            {
                Log.Information("Roots: raw={RawRoot}, mods={ModsRoot}, workspace folders={FolderCount}",
                    rootConfig.RawRoot, rootConfig.ModsRoot, rootConfig.WorkspaceFolders.Length);
            }

            Log.Information("Initialize received from {ClientName}", request.ClientInfo?.Name ?? "unknown client");
            return Task.CompletedTask;
        })
        .OnInitialized((languageServer, request, response, cancellationToken) =>
        {
            // Declared explicitly rather than left to the protocol default. Every range this
            // server produces comes from SourceText, which indexes UTF-16 code units, so a
            // client negotiating UTF-8 offsets would silently mis-place ranges in any file
            // containing astral characters. UTF-16 is the encoding every LSP client supports.
            response.Capabilities.PositionEncoding = PositionEncodingKind.UTF16;

            Log.Information("GSCode v2 server initialized");
            return Task.CompletedTask;
        })
        .OnStarted((languageServer, cancellationToken) =>
        {
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
                        string identity = ServerBuildIdentity.Compute(BundledDataFilePaths());
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
                        // Let the connection's output pump settle before the first progress
                        // notification; sending in the initialize/initialized window can drop
                        // notifications on a workspace small enough to index in a few ms.
                        await Task.Delay(500, CancellationToken.None);
                        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
                        IndexOutcome outcome = await indexer.IndexAsync(mode, notifier, CancellationToken.None);
                        stopwatch.Stop();
                        Log.Information(
                            "Workspace indexing complete: {Count} files in {Seconds:F1}s ({Restored:N0} from cache)",
                            outcome.Total,
                            stopwatch.Elapsed.TotalSeconds,
                            outcome.Restored);
                        if ( outcome.SkippedOversized > 0 )
                        {
                            Log.Warning(
                                "{Count} file(s) skipped: larger than the {Limit} MB analysis limit",
                                outcome.SkippedOversized,
                                WorkspaceIndexer.MaxAnalysedCharacters / (1024 * 1024));
                        }

                        LogIndexBreakdown(languageServer.Services.GetRequiredService<ScriptDatabase>());

                        // Only now: the cross-file picture is complete, and a record's stored
                        // diagnostics are meaningless for files still waiting to be analysed.
                        languageServer.Services.GetRequiredService<WorkspaceDiagnosticsPublisher>().Refresh();

                        // Sampled before the monitor starts, so the number reflects the state
                        // indexing left behind rather than anything steady-state traffic did.
                        LogMemoryReport("indexing", outcome);

                        if ( CompactIfFragmented() )
                        {
                            LogMemoryReport("compaction", outcome);
                        }

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
Log.Information("GSCode v2 server exited");
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
        foreach ( NamespaceSpan span in record.Namespaces )
        {
            namespaces.Add(span.KeyName);
        }
    }

    foreach ( ScriptRecord record in database.Csc.AllRecords )
    {
        CategorizeContext(record.ContextId, ref cscRaw, ref cscMod, ref cscWorkspace);
        functions += record.Functions.Length;
        classes += record.Classes.Length;
        macros += record.Macros.Length;
        foreach ( NamespaceSpan span in record.Namespaces )
        {
            namespaces.Add(span.KeyName);
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

    Log.Information("{IndexReport}", report.ToString());
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
static bool CompactIfFragmented()
{
    const long fragmentationThresholdBytes = 32L * 1024 * 1024;

    long fragmented = GC.GetGCMemoryInfo().FragmentedBytes;
    if ( fragmented < fragmentationThresholdBytes )
    {
        return false;
    }

    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    return true;
}

// A one-shot memory breakdown, logged at the indexing -> serving transition.
//
// The point is the gap between the managed heap and the working set. Cold indexing allocates
// heavily per file (source text, token arrays, AST, extraction builders) at
// ProcessorCount - 1 way parallelism, all of it garbage once the record is built; a warm
// restore just deserializes records. If the LIVE numbers match across a cold and a warm start
// while the working set differs, the extra footprint is grown, uncompacted heap rather than
// retained data — and a one-time compacting collect here is the fix.
/// <summary>
/// The detailed memory breakdown, at Verbose.
///
/// Gated by the LOG LEVEL rather than an environment variable. A setting the user can change from
/// the settings UI beats one that needs an env var and a restart — and GSCODE_INSTRUMENTATION was
/// doubly confusing, since PerfTracker already uses that name as a COMPILE-TIME symbol for
/// something else entirely.
///
/// Called twice a session, after indexing and after compaction, so it is not the noisy part.
/// </summary>
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
    report.AppendLine($"    fragmented      {fragmented,8:F1} MB   (mostly large-object heap)");
    report.Append($"    collections     gen0 {GC.CollectionCount(0):N0} · gen1 {GC.CollectionCount(1):N0} · gen2 {GC.CollectionCount(2):N0}");

    Log.Verbose("{MemoryReport}", report.ToString());
}


// Locates the bundled data files whose contents feed the server build identity.
static IEnumerable<string> BundledDataFilePaths()
{
    string apiDirectory = Path.Combine(AppContext.BaseDirectory, "Api");
    yield return Path.Combine(apiDirectory, "t7_api_gsc.json");
    yield return Path.Combine(apiDirectory, "t7_api_csc.json");
    yield return Path.Combine(apiDirectory, "t7_stock_scripts.txt");
}
