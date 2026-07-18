using CommandLine;
using GSCode.Core;
using GSCode.Server.Configuration;
using GSCode.Server.Handlers;
using GSCode.Server.Logging;
using GSCode.Server.Transport;
using GSCode.Workspace.Documents;
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
        })
        .AddHandler<TextSyncHandler>()
        .AddHandler<DocumentSymbolHandler>()
        .AddHandler<FoldingRangeHandler>()
        .AddHandler<SelectionRangeHandler>()
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

            RootConfig rootConfig = RootConfig.Create(
                settings.RawEnabled,
                settings.RawPathOverride.Length == 0 ? null : settings.RawPathOverride,
                settings.ModsPathOverride.Length == 0 ? null : settings.ModsPathOverride,
                Environment.GetEnvironmentVariable("TA_TOOLS_PATH"),
                workspaceFolders,
                fileSystem);

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
            Log.Information("GSCode v2 server initialized");
            return Task.CompletedTask;
        });
});

await server.WaitForExit;

transport.Owner?.Dispose();
Log.Information("GSCode v2 server exited");
await Log.CloseAndFlushAsync();
