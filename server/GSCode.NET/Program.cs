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


Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.WriteTo.Console()
#if DEBUG
				.WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
#endif
				.CreateLogger();

Log.Information("GSCode Language Server");

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

Log.Information("GSCode Language Server");

ServerOptions serverOptions = new();
Parser.Default.ParseArguments<ServerOptions>(args).WithParsed(o => serverOptions = o);

(Stream input, Stream output, IDisposable? disposable) = await StreamResolver.ResolveAsync(serverOptions, CancellationToken.None);

LanguageServer server = await LanguageServer.From(options =>
{
	options
		.WithInput(input)
		.WithOutput(output)
		.WithConfigurationSection("gscode")
		.ConfigureLogging(
			x => x
				.AddSerilog(Log.Logger)
				.AddLanguageProtocolLogging()
				.SetMinimumLevel(LogLevel.Debug)
		)
		.WithServices(x => x.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)))
		.WithServices(services =>
		{
			// Inject ScriptManager with ILanguageServerFacade so it can publish diagnostics during indexing
			services.AddSingleton<ScriptManager>(sp =>
			{
				var logger = sp.GetRequiredService<ILogger<ScriptManager>>();
				var facade = sp.GetRequiredService<ILanguageServerFacade>();
				return new ScriptManager(logger, facade);
			});
			services.AddSingleton(new TextDocumentSelector(
                new TextDocumentFilter { Pattern = "**/*.gsc" },
                new TextDocumentFilter { Pattern = "**/*.csc" },
                new TextDocumentFilter { Pattern = "**/*.gsh" }
            ));
		})
		.OnInitialize(async (server, request, ct) =>
		{
			try
			{
				// Check if workspace indexing is enabled via InitializationOptions
				// This is passed by the client during the initialize request, so no need
				// to make a request back to the client (which would cause warnings)
				bool enableIndexing = false;

				if (request.InitializationOptions is JToken initOptions)
				{
					var gscodeSection = initOptions.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						var indexingSetting = gscodeSection.SelectToken("enableWorkspaceIndexing");
						if (indexingSetting is not null)
						{
							enableIndexing = indexingSetting.Value<bool>();
						}
					}
				}

				if (!enableIndexing)
				{
					Log.Information("Workspace indexing is disabled (not enabled in initialization options)");
					return;
				}

				Log.Information("Workspace indexing is enabled via initialization options");

				var sm = server.Services.GetRequiredService<ScriptManager>();

				// Use a long-lived CTS for indexing; do not tie to Initialize request token
				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				// Defer actual indexing to OnInitialized to ensure server is fully ready
				await Task.CompletedTask;
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to process initialization options");
			}
		})
		.OnInitialized(async (server, request, response, ct) =>
		{
			try
			{
				// Check initialization options again for workspace indexing
				bool enableIndexing = false;

				if (request.InitializationOptions is JToken initOptions)
				{
					var gscodeSection = initOptions.SelectToken("gscode");
					if (gscodeSection is not null)
					{
						var indexingSetting = gscodeSection.SelectToken("enableWorkspaceIndexing");
						if (indexingSetting is not null)
						{
							enableIndexing = indexingSetting.Value<bool>();
						}
					}
				}

				if (!enableIndexing)
				{
					return;
				}

				var sm = server.Services.GetRequiredService<ScriptManager>();

				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				if (request.WorkspaceFolders is not null && request.WorkspaceFolders.Any())
				{
					foreach (var wf in request.WorkspaceFolders)
					{
						string root = wf.Uri.ToUri().LocalPath;
						Log.Information("Starting workspace indexing for: {Root}", root);
						_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), CancellationToken.None);
					}
				}
				else if (request.RootUri is not null)
				{
					string root = request.RootUri.ToUri().LocalPath;
					Log.Information("Starting workspace indexing for: {Root}", root);
					_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), CancellationToken.None);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, "Failed to start workspace indexing");
			}
		})
		.AddHandler<TextDocumentSyncHandler>()
		.AddHandler<SemanticTokensHandler>()
		.AddHandler<HoverHandler>()
		.AddHandler<CompletionHandler>()
		.AddHandler<FoldingRangeHandler>()
		.AddHandler<DefinitionHandler>()
		.AddHandler<DocumentSymbolHandler>()
		.AddHandler<SignatureHelpHandler>()
		.AddHandler<ReferencesHandler>();
	// Allow disposal of the stream if required.
	if (disposable is not null)
	{
		options.RegisterForDisposal(disposable);
	}
}).ConfigureAwait(false);


Log.Information("Language server connected successfully!");

#if FLAG_MEMORY_DEBUG
// Memory monitoring
var memoryMonitorCts = new CancellationTokenSource();
_ = Task.Run(async () =>
{
	while (!memoryMonitorCts.Token.IsCancellationRequested)
	{
		try
		{
			var process = Process.GetCurrentProcess();
			var memoryMB = process.WorkingSet64 / 1024.0 / 1024.0;
			var privateMemoryMB = process.PrivateMemorySize64 / 1024.0 / 1024.0;
			Log.Debug("Memory Usage - Working Set: {WorkingSet:F2} MB, Private: {Private:F2} MB", memoryMB, privateMemoryMB);

			await Task.Delay(250, memoryMonitorCts.Token);
		}
		catch (OperationCanceledException)
		{
			break;
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error monitoring memory");
		}
	}
}, memoryMonitorCts.Token);
#endif

await server.WaitForExit;

#if FLAG_MEMORY_DEBUG
memoryMonitorCts.Cancel();
#endif