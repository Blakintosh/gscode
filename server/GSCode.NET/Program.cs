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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

//IObserver<WorkDoneProgressReport> workDone = null!;

Log.Information("GSCode Language Server");

// Load GSC API into the SPA
try
{
    if (File.Exists(@"api/t7_api_gsc.json"))
    {
        ScriptAnalyserData.LoadLanguageApiLibrary(File.ReadAllText(@"api/t7_api_gsc.json"));
    }
}
catch(Exception) { }

// Load CSC API into the SPA
try
{
    if (File.Exists(@"api/t7_api_csc.json"))
    {
        ScriptAnalyserData.LoadLanguageApiLibrary(File.ReadAllText(@"api/t7_api_csc.json"));
    }
}
catch (Exception) { }

// Get the standard input and output streams.
Stream stdin = Console.OpenStandardInput();
Stream stdout = Console.OpenStandardOutput();

// Create a JSON RPC message handler with these streams.
var handler = new HeaderDelimitedMessageHandler(stdout, stdin);
var server = new LanguageServer(stdin, stdout);

// Link the server to the message handler, and start handling messages.
var rpc = server.Rpc;

Log.Information("Listening has started");

await rpc.Completion;

Log.Information("Stopped");