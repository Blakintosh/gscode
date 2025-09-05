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
			services.AddSingleton<ScriptManager>();
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
		.AddHandler<TextDocumentSyncHandler>()
		.AddHandler<SemanticTokensHandler>()
		.AddHandler<HoverHandler>()
		.AddHandler<CompletionHandler>()
		.AddHandler<FoldingRangeHandler>();

	// Allow disposal of the stream if required.
	if (disposable is not null)
	{
		options.RegisterForDisposal(disposable);
	}
}).ConfigureAwait(false);


Log.Information("Language server connected successfully!");

await server.WaitForExit;