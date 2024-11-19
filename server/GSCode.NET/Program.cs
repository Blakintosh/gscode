/**
	GSCode.NET Language Server
    Copyright (C) 2022 Blakintosh

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using GSCode.NET;
using GSCode.Parser.SPA;
using Serilog;
using StreamJsonRpc;
using System.IO.Pipes;
using System.Text;
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

var server = await LanguageServer.From(options =>
	options
		.WithInput(Console.OpenStandardInput())
		.WithOutput(Console.OpenStandardOutput())
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
).ConfigureAwait(false);


Log.Information("Language server connected successfully!");