using CommandLine;
using CommandLine.Text;
using System.Runtime;
using GSCode.Core;
using GSCode.Server.Composition;
using GSCode.Server.Configuration;
using GSCode.Server.Logging;
using GSCode.Server.Startup;
using GSCode.Server.Transport;
using GSCode.Workspace.Resolution;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// All server logging goes to STDERR: the pipe-transport client surfaces stderr in the
// "GSCode Server" output channel, and stdout must stay clean for the stdio transport.
LoggingLevelSwitch levelSwitch = new(LogEventLevel.Information);

// Owns the startup-floor lifecycle: what the client asked for, and when the channel is allowed to
// drop to it. See its own remarks for why this exists rather than a Program.cs-local pair of
// variables — OnInitialize (in ServerServices) and the startup task's finally block (in
// StartupIndexRunner) both need to touch this state, and neither can see a top-level local here.
StartupLogController logController = new(levelSwitch);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
    .CreateLogger();

Log.Information("GSCode {Version} language server starting", IndexReporting.ServerVersion());

// The GC configuration this process actually got, which is not the same question as what the
// repository's runtimeconfig template says. The template is baked into runtimeconfig.json at
// PUBLISH time, so an extension running a bundle from before a template change runs the old
// settings however recently the server project itself was rebuilt — and the difference between
// Workstation and four-heap Server GC is a cold index of 2.2 s against 0.8 on bo3. That took a
// while to spot from timings alone; it is one line to state.
Log.Information(
    "GC: {Mode}, {Heaps} heap(s), {Processors} processors",
    GCSettings.IsServerGC ? "server" : "workstation",
    AppContext.GetData("System.GC.HeapCount") ?? "one per core",
    Environment.ProcessorCount);

// HelpWriter is silenced because the default one writes to STDOUT, and stdout is the stdio
// transport's wire. A --help that corrupts the protocol is worse than no --help, so the text is
// rendered to stderr below instead.
TransportOptions transportOptions = new();
bool argumentsUsable = false;
bool helpRequested = false;

using ( CommandLine.Parser argumentParser = new(config => config.HelpWriter = null) )
{
    ParserResult<TransportOptions> parseResult = argumentParser.ParseArguments<TransportOptions>(args);

    parseResult
        .WithParsed(parsed =>
        {
            transportOptions = parsed;
            argumentsUsable = true;
        })
        .WithNotParsed(errors =>
        {
            // Anything unrecognised used to leave transportOptions at its defaults and fall through
            // to the stdio branch, so a typo — or a flag a newer client passes to an older server —
            // silently produced a stdio server while the client waited on a named pipe. Nothing was
            // logged, and the symptom was a language server that never answered.
            helpRequested = errors.Any(static error =>
                error is HelpRequestedError or HelpVerbRequestedError or VersionRequestedError);

            if ( helpRequested )
            {
                Console.Error.WriteLine(HelpText.AutoBuild(parseResult, static help => help, static example => example));
                return;
            }

            foreach ( Error error in errors )
            {
                Log.Fatal("Bad command line: {Error}", error.ToString());
            }
        });
}

if ( !argumentsUsable )
{
    await Log.CloseAndFlushAsync();
    return helpRequested ? 0 : 1;
}

// Before anything resolves the bundled data: those singletons read whichever game is active when
// they are first requested, and that happens during container construction.
if ( !string.IsNullOrWhiteSpace(transportOptions.Game) )
{
    if ( !GameProfile.Select(transportOptions.Game) )
    {
        // Loudly, because the symptom otherwise is "gscode.game does nothing": the setting reads
        // back exactly as written while the server runs as BO3, and nothing disagrees anywhere.
        Log.Warning(
            "Game {Requested} is not a supported dialect — falling back to bo3. Supported: {Supported}",
            transportOptions.Game,
            string.Join(", ", GameProfile.All.Where(static p => p.Supported).Select(static p => p.ShortName)));
    }

    Log.Information("Game profile: {Game} ({Display})", GameProfile.Active.ShortName, GameProfile.Active.DisplayName);
}

// Wrapped, because everything below this line depends on it and nothing above it logs. An
// exception escaping here used to reach the top level unlogged, past the flush at the end of the
// file, so the one message explaining why the server did not start was the one message lost.
TransportResolver.ResolvedTransport transport;
try
{
    transport = await TransportResolver.ResolveAsync(transportOptions, CancellationToken.None);
}
catch ( Exception exception )
{
    Log.Fatal(exception, "Could not connect the transport");
    await Log.CloseAndFlushAsync();
    return 1;
}

// Which transport is in use is the first thing a "the extension says the server never started"
// report needs, and nothing said it.
Log.Information("Transport: {Transport}", transport.Description);

ServerSettings settings = new();
PhysicalFileSystem fileSystem = new();
ResolverHolder resolverHolder = new(fileSystem);

// Owns the lifetime of the persistent cache; created in OnStarted, drained on exit. A holder
// rather than a local, so the gscode/clearCache handler can close and delete it on request.
CacheHolder cacheHolder = new();

// Owns the startup indexing task's cancellation, so exit and gscode/clearCache can both stop it
// rather than letting it run detached against a cache that is about to close underneath it.
IndexingLifetime indexingLifetime = new();

// Shared by every notification the server sends unprompted early in the connection's life —
// serverReady, the indexing family, a restored tab's gameMismatch — so exactly one clock decides
// when the pipe has had a moment to settle, rather than each caller starting (or forgetting to
// start) its own. See its own remarks for why that used to be indexing's alone.
ConnectionSettleGate settleGate = new();

// The OnStarted work: mode selection, opening the workspace cache, and — when indexing is not
// off — the whole startup indexing task. See StartupIndexRunner's own remarks for why this is a
// class rather than more top-level statements here.
StartupIndexRunner startupIndexRunner = new(settings, resolverHolder, cacheHolder, indexingLifetime, settleGate, logController);

LanguageServer server = await LanguageServer.From(options =>
    ServerServices.Configure(
        options.WithInput(transport.Input).WithOutput(transport.Output),
        settings,
        levelSwitch,
        fileSystem,
        resolverHolder,
        cacheHolder,
        indexingLifetime,
        settleGate,
        logController,
        startupIndexRunner));

await server.WaitForExit;

// Stop the startup task and give it a bounded moment to actually finish BEFORE closing the
// cache it may still be writing to — closing first raced the two, and every enqueue that landed
// after close was silently counted as dropped rather than persisted. Bounded rather than awaited
// outright: a task that ignores cancellation must not hang shutdown, and this is best-effort
// next to it — a killed process loses only whatever was in flight at that instant either way.
await indexingLifetime.CancelAndWaitAsync(TimeSpan.FromSeconds(5));

// Drain the cache writer so the last records land before we close. A no-op when
// gscode/clearCache already closed it.
await cacheHolder.CloseAsync();

transport.Owner?.Dispose();
Log.Information("GSCode {Version} server exited", IndexReporting.ServerVersion());
await Log.CloseAndFlushAsync();

// An explicit exit code, because the failure paths above return 1 — a bad command line, or a
// transport that never connected. Without one the process reports success for both.
return 0;
