using GSCode.NET;
using GSCode.Parser.SPA;
using Serilog;
using StreamJsonRpc;
using System.IO.Pipes;
using System.Text;
using CommandLine;
using GSCode.NET.LSP;
using GSCode.NET.LSP.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using GSCode.NET.Configuration;
using Serilog.Core;
using Serilog.Events;

// Create the dynamic logging level switch FIRST
LoggingLevelSwitch levelSwitch = new(LogEventLevel.Information);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.FromLogContext()
    .WriteTo.Console()
#if DEBUG
    .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
#endif
    .CreateLogger();

Log.Information("GSCode Language Server starting...");

// Determine the base directory of the executing assembly
string assemblyLocation = Assembly.GetExecutingAssembly().Location;
string? assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
string apiDirectory = assemblyDirectory is null ? "api" : Path.Combine(assemblyDirectory, "api");

string gscApiPath = Path.Combine(apiDirectory, "t7_api_gsc.json");
string cscApiPath = Path.Combine(apiDirectory, "t7_api_csc.json");

// Load GSC & CSC API into the SPA
await ScriptAnalyserData.LoadLanguageApiAsync(
    "https://www.gscode.net/api/getLibrary?gameId=t7&languageId=gsc",
    gscApiPath
);
await ScriptAnalyserData.LoadLanguageApiAsync(
    "https://www.gscode.net/api/getLibrary?gameId=t7&languageId=csc",
    cscApiPath
);

Log.Information("Game script API metadata loaded.");

ServerOptions serverOptions = new();
Parser.Default.ParseArguments<ServerOptions>(args).WithParsed(o => serverOptions = o);

(Stream input, Stream output, IDisposable? disposable) = await StreamResolver.ResolveAsync(serverOptions, CancellationToken.None);

LanguageServer server = await LanguageServer.From(options =>
{
    options
        .WithInput(input)
        .WithOutput(output)
        .WithConfigurationSection("gscode")
        .ConfigureLogging(x => x
            .AddSerilog(Log.Logger)
            .AddLanguageProtocolLogging()
            .SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug))
        .WithServices(s =>
        {
            s.AddSingleton(levelSwitch);
            s.AddSingleton<ServerConfiguration>();
            s.AddSingleton<ScriptManager>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<ScriptManager>>();
                var facade = sp.GetRequiredService<ILanguageServerFacade>();
                return new ScriptManager(logger, facade);
            });
            s.AddSingleton(new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.gsc" },
                new TextDocumentFilter { Pattern = "**/*.csc" },
                new TextDocumentFilter { Pattern = "**/*.gsh" }
            ));
        })
        .OnInitialize(async (server, request, ct) =>
        {
            var cfg = server.Services.GetRequiredService<ServerConfiguration>();
            var switchRef = server.Services.GetRequiredService<LoggingLevelSwitch>();

            void ApplyTrace()
            {
                switchRef.MinimumLevel = cfg.TraceServer switch
                {
                    TraceServerLevel.Off => LogEventLevel.Warning,       // suppress routine info
                    TraceServerLevel.Messages => LogEventLevel.Information,
                    TraceServerLevel.Verbose => LogEventLevel.Debug,
                    _ => LogEventLevel.Warning
                };
                Log.Debug("Applied trace server level: {Level} (Serilog min now {Min})", cfg.TraceServer, switchRef.MinimumLevel);
            }

            cfg.ApplyInitializationOptions(request.InitializationOptions);
            ApplyTrace();
            cfg.Changed += _ => ApplyTrace();

            if (cfg.DisableIndexOnInitialize)
            {
                Log.Information("Workspace indexing skipped (disableIndexOnInitialize=true).");
                return;
            }

            try
            {
                var sm = server.Services.GetRequiredService<ScriptManager>();
                var indexingCts = new CancellationTokenSource();
                options.RegisterForDisposal(indexingCts);
                var indexingToken = indexingCts.Token;

                if (request.WorkspaceFolders is { } wfs && wfs.Any())
                {
                    foreach (var wf in wfs)
                    {
                        string root = wf.Uri.ToUri().LocalPath;
                        Log.Debug("Queue indexing workspace folder: {Root}", root);
                        _ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), CancellationToken.None);
                    }
                }
                else if (request.RootUri is not null)
                {
                    string root = request.RootUri.ToUri().LocalPath;
                    Log.Debug("Queue indexing single root: {Root}", root);
                    _ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start workspace indexing");
            }
        })
        .AddHandler<ConfigurationDidChangeHandler>()
        .AddHandler<TextDocumentSyncHandler>()
        .AddHandler<SemanticTokensHandler>()
        .AddHandler<HoverHandler>()
        .AddHandler<CompletionHandler>()
        .AddHandler<FoldingRangeHandler>()
        .AddHandler<DefinitionHandler>()
        .AddHandler<DocumentSymbolHandler>()
        .AddHandler<SignatureHelpHandler>()
        .AddHandler<ReferencesHandler>()
        .AddHandler<WorkspaceDidChangeWatchedFilesHandler>();

    if (disposable is not null)
    {
        options.RegisterForDisposal(disposable);
    }
}).ConfigureAwait(false);

Log.Information("Language server initialized and ready.");

await server.WaitForExit;