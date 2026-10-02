using GSCode.Core;
using GSCode.Core.Symbols;
using GSCode.Server.Configuration;
using GSCode.Server.Formatting;
using GSCode.Server.Handlers;
using GSCode.Server.Logging;
using GSCode.Server.Startup;
using GSCode.Workspace.Api;
using GSCode.Workspace.Completion;
using GSCode.Workspace.Database;
using GSCode.Workspace.Documents;
using GSCode.Workspace.Indexing;
using GSCode.Workspace.Resolution;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using Serilog.Core;

namespace GSCode.Server.Composition;

/// <summary>
/// The container: what OmniSharp resolves for every handler, the handler chain itself, and the
/// two lightweight initialize hooks. Extracted from <c>Program.cs</c>'s top-level statements so the
/// entry point is readable without scrolling past two hundred lines of DI registration first.
///
/// <c>OnStarted</c> is deliberately NOT here — see <see cref="StartupIndexRunner"/> — because it
/// needs the cache and indexing machinery this class has no other reason to touch.
/// </summary>
internal static class ServerServices
{
    /// <summary>
    /// Registers every singleton the handlers resolve, then the handler chain, then the
    /// initialize/initialized hooks, then <c>OnStarted</c> via <paramref name="startupIndexRunner"/>.
    /// </summary>
    public static LanguageServerOptions Configure(
        LanguageServerOptions options,
        ServerSettings settings,
        LoggingLevelSwitch levelSwitch,
        PhysicalFileSystem fileSystem,
        ResolverHolder resolverHolder,
        CacheHolder cacheHolder,
        IndexingLifetime indexingLifetime,
        ConnectionSettleGate settleGate,
        StartupLogController logController,
        StartupIndexRunner startupIndexRunner)
    {
        return options
            .WithServices(services => RegisterServices(
                services, settings, levelSwitch, fileSystem, resolverHolder, cacheHolder, indexingLifetime, settleGate))
            .AddHandler<TextSyncHandler>()
            .AddHandler<DocumentSymbolHandler>()
            .AddHandler<FoldingRangeHandler>()
            .AddHandler<SelectionRangeHandler>()
            .AddHandler<WorkspaceSymbolHandler>()
            .AddHandler<WatchedFilesHandler>()
            .AddHandler<WorkspaceFoldersHandler>()
            .AddHandler<PlanRenameHandler>()
            .AddHandler<ClearCacheHandler>()
            .AddHandler<SupportedGamesHandler>()
            .AddHandler<BuiltinAtHandler>()
            .AddHandler<GenerateScriptDocHandler>()
            .AddHandler<HoverHandler>()
            .AddHandler<DefinitionHandler>()
            .AddHandler<ImplementationHandler>()
            .AddHandler<TypeDefinitionHandler>()
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
                OnInitializeAsync(request, settings, logController, resolverHolder, fileSystem))
            .OnInitialized((languageServer, request, response, cancellationToken) =>
                OnInitializedAsync(response))
            .OnStarted((languageServer, cancellationToken) =>
                startupIndexRunner.RunAsync(
                    languageServer.Services,
                    languageServer.Services.GetRequiredService<ILanguageServerFacade>(),
                    cancellationToken));
    }

    private static void RegisterServices(
        IServiceCollection services,
        ServerSettings settings,
        LoggingLevelSwitch levelSwitch,
        PhysicalFileSystem fileSystem,
        ResolverHolder resolverHolder,
        CacheHolder cacheHolder,
        IndexingLifetime indexingLifetime,
        ConnectionSettleGate settleGate)
    {
        services.AddSingleton(settings);
        services.AddSingleton(levelSwitch);
        services.AddSingleton<IFileSystem>(fileSystem);

        // Shared across the whole session. Keyed by resolved path, so a mod's header and the
        // raw one it shadows stay separate entries.
        services.AddSingleton<InsertCache>();
        services.AddSingleton(resolverHolder);
        services.AddSingleton(cacheHolder);
        services.AddSingleton(indexingLifetime);
        services.AddSingleton(settleGate);
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
                path =>
                {
                    // Read ONCE per file rather than twice: a swap landing between two reads of a
                    // volatile field paired a resolver with a DIFFERENT resolver's context, which
                    // is wrong in the specific way that matters here — the context decides how the
                    // path resolves. Reading it into a local before either use is what makes the
                    // two agree, however many workspace-folder or clearCache events land in between
                    // one file's open and the next.
                    PathResolver current = holder.Current;
                    return new ResolverInsertProvider(current, current.GetContext(path), files, inserts);
                },
                provider.GetRequiredService<NameTable>(),
                inserts);
        });

        services.AddSingleton(provider =>
            new DiagnosticsPublisher(provider.GetRequiredService<ILanguageServerFacade>()));
        services.AddSingleton<WorkspaceDiagnosticsPublisher>();

        // The one place the cross-file lint pipeline is called from. Registered before its two
        // consumers only for readability — the container resolves in dependency order.
        services.AddSingleton<DocumentLinter>();
        services.AddSingleton<WorkspaceLintSweep>();
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
            provider.GetRequiredService<ResolverHolder>(),
            provider.GetRequiredService<BuiltinApiSet>()));
        services.AddSingleton<FormattingSupport>();
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
    }

    private static Task OnInitializeAsync(
        InitializeParams request,
        ServerSettings settings,
        StartupLogController logController,
        ResolverHolder resolverHolder,
        PhysicalFileSystem fileSystem)
    {
        if ( request.InitializationOptions is JToken initializationOptions )
        {
            settings.Apply(initializationOptions);

            // A safety net for hosts that do not pass --game: the profile is normally chosen
            // from the command line before the container is built, because the bundled data
            // resolves during construction and would otherwise load for the default game.
            GameProfile.Select(settings.Game);
        }

        // Outside the branch above, so a host that sends no initialization options cannot leave
        // the level controller and ServerSettings.ServerLogLevel disagreeing about what the
        // level is. SetRequested applies the startup floor itself — see StartupLogController.
        logController.SetRequested(ServerLogLevel.FromSetting(settings.ServerLogLevel));

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
    }

    private static Task OnInitializedAsync(InitializeResult response)
    {
        // Declared explicitly rather than left to the protocol default. Every range this
        // server produces comes from SourceText, which indexes UTF-16 code units, so a
        // client negotiating UTF-8 offsets would silently mis-place ranges in any file
        // containing astral characters. UTF-16 is the encoding every LSP client supports.
        response.Capabilities.PositionEncoding = PositionEncodingKind.UTF16;

        Log.Information("GSCode {Version} server initialized", IndexReporting.ServerVersion());
        return Task.CompletedTask;
    }

    // Whether a resolved root is the one the user asked for, or one worked out from the workspace.
    // Compares the paths rather than just testing whether the setting is non-empty, so a setting
    // naming a folder that is not on disk - which falls back to derivation - reports honestly.
    private static string RootSource(string configured, string? resolved)
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

    // The settings that shape behaviour, logged once at startup at Information — so they are in the
    // log a user attaches to a bug report without anyone having to ask for a higher level first.
    private static void LogEffectiveSettings(ServerSettings settings)
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

    // Locates the bundled data files whose contents feed the server build identity — the active
    // game's set, named by its profile, so a dialect port's data invalidates the cache like BO3's.
    // The three data loads, wrapped so each says what it looked for and what it got. GSCode.Workspace
    // carries no Serilog reference by design, so the reporting lives here at the call site.
    //
    // Worth logging loudly: a missing data file is NOT an error to the loaders — it means "this game
    // ships none" — so a game whose files failed to deploy behaves exactly like a game that has none,
    // and every engine function silently becomes unknown. That failure mode is invisible without this.
    private static BuiltinApiSet LoadBuiltinApi()
    {
        GameProfile game = GameProfile.Active;
        string directory = Path.Combine(AppContext.BaseDirectory, "Api");
        LogDataFile(game, directory, game.ApiFileName(ScriptLanguage.Gsc), "builtin API (gsc)");
        if ( game.HasClientScripts )
        {
            LogDataFile(game, directory, game.ApiFileName(ScriptLanguage.Csc), "builtin API (csc)");
        }

        BuiltinApiSet set = BuiltinApiSet.Load(directory, game, static (path, exception) =>
            Log.Warning(exception, "Builtin API file {Path} failed to parse; treating it as empty", path));
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

    private static ObjectFields LoadObjectFields()
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

    private static StockScripts LoadStockScripts()
    {
        GameProfile game = GameProfile.Active;
        string directory = Path.Combine(AppContext.BaseDirectory, "Api");
        LogDataFile(game, directory, game.StockScriptsFileName, "stock scripts");

        return StockScripts.Load(directory, game);
    }

    // Reports one data file: what the profile asked for, and whether it is actually there.
    private static void LogDataFile(GameProfile game, string directory, string? fileName, string what)
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
}
