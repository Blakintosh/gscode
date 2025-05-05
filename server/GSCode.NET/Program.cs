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


Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Debug()
				.WriteTo.Console()
				//.WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
				.CreateLogger();

//IObserver<WorkDoneProgressReport> workDone = null!;

Log.Information("GSCode Language Server");

// Load GSC API into the SPA
// try
// {
//     if (File.Exists(@"api/t7_api_gsc.json"))
//     {
//         ScriptAnalyserData.LoadLanguageApiLibrary(File.ReadAllText(@"api/t7_api_gsc.json"));
//     }
// }
// catch(Exception) { }
//
// // Load CSC API into the SPA
// try
// {
//     if (File.Exists(@"api/t7_api_csc.json"))
//     {
//         ScriptAnalyserData.LoadLanguageApiLibrary(File.ReadAllText(@"api/t7_api_csc.json"));
//     }
// }
// catch (Exception) { }

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
		.AddHandler<CompletionHandler>();

	// Allow disposal of the stream if required.
	if (disposable is not null)
	{
		options.RegisterForDisposal(disposable);
	}
}).ConfigureAwait(false);


Log.Information("Language server connected successfully!");

await server.WaitForExit;