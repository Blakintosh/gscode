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

//IObserver<WorkDoneProgressReport> workDone = null!;

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
				new TextDocumentFilter()
				{
					Pattern = "**/*.gsc"
				},
				new TextDocumentFilter()
				{
					Pattern = "**/*.csc"
				}
			));
		})
		.OnInitialize(async (server, request, ct) =>
		{
			try
			{
                // Read client-provided setting to optionally disable indexing on initialize
                var disableIndexOnInitialize = false;
                try
                {
                    if (request.InitializationOptions is not null)
                    {
                        var token = request.InitializationOptions as JToken ?? JToken.FromObject(request.InitializationOptions);
                        disableIndexOnInitialize =
                            token["disableIndexOnInitialize"]?.Value<bool?>()
                            ?? token["gscode"]?["disableIndexOnInitialize"]?.Value<bool?>()
                            ?? false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Unable to parse initializationOptions; proceeding with defaults.");
                }

                if (disableIndexOnInitialize)
                {
                    Log.Information("Skipping workspace indexing on initialize per client setting.");
                    return;
                }

                var sm = server.Services.GetRequiredService<ScriptManager>();

				// Use a long-lived CTS for indexing; do not tie to Initialize request token
				var indexingCts = new CancellationTokenSource();
				options.RegisterForDisposal(indexingCts);
				var indexingToken = indexingCts.Token;

				if (request.WorkspaceFolders is not null && request.WorkspaceFolders.Any())
				{
					foreach (var wf in request.WorkspaceFolders)
					{
						string root = wf.Uri.ToUri().LocalPath;
						_ = Task.Run(() => sm.IndexWorkspaceAsync(root, indexingToken), CancellationToken.None);
					}
				}
				else if (request.RootUri is not null)
				{
					string root = request.RootUri.ToUri().LocalPath;
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

await server.WaitForExit;