using CommandLine;
using GSCode.Server.Configuration;
using GSCode.Server.Logging;
using GSCode.Server.Transport;
using Newtonsoft.Json.Linq;
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

LanguageServer server = await LanguageServer.From(options =>
{
    options
        .WithInput(transport.Input)
        .WithOutput(transport.Output)
        .OnInitialize((languageServer, request, cancellationToken) =>
        {
            if ( request.InitializationOptions is JToken initializationOptions )
            {
                string? configuredLogLevel = InitializationOptionsReader.ReadServerLogLevel(initializationOptions);
                levelSwitch.MinimumLevel = ServerLogLevel.FromSetting(configuredLogLevel);
            }

            Log.Information("Initialize received from {ClientName}", request.ClientInfo?.Name ?? "unknown client");
            return Task.CompletedTask;
        })
        .OnInitialized((languageServer, request, response, cancellationToken) =>
        {
            Log.Information("GSCode v2 server initialized");
            return Task.CompletedTask;
        });
});

await server.WaitForExit;

transport.Owner?.Dispose();
Log.Information("GSCode v2 server exited");
await Log.CloseAndFlushAsync();
