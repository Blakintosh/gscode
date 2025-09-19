using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace GSCode.NET.Configuration;

public enum TraceServerLevel
{
    Off,
    Messages,
    Verbose
}

public sealed class ServerConfiguration
{
    private readonly ILogger<ServerConfiguration> _logger;
    private readonly object _gate = new();

    public ServerConfiguration(ILogger<ServerConfiguration> logger)
    {
        _logger = logger;
    }

    // Initialization-only (reload required to take effect after change)
    public bool DisableIndexOnInitialize { get; private set; }
    public TraceServerLevel TraceServer { get; private set; } = TraceServerLevel.Off;

    public event Action<ServerConfiguration>? Changed;

    public void ApplyInitializationOptions(object? initOptions)
    {
        try
        {
            if (initOptions is null) return;
            var token = initOptions as JToken ?? JToken.FromObject(initOptions);
            UpdateFromToken(token, source: "initialize");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse initializationOptions.");
        }
    }

    public void ApplyDidChangeConfiguration(object? settings)
    {
        try
        {
            if (settings is null) return;
            var token = settings as JToken ?? JToken.FromObject(settings);
            // Do NOT update DisableIndexOnInitialize here (requires restart)
            UpdateFromToken(token, source: "didChangeConfiguration");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse workspace configuration change.");
        }
    }

    private static TraceServerLevel ParseTraceServer(string? value, TraceServerLevel fallback) =>
        value?.ToLowerInvariant() switch
        {
            "messages" => TraceServerLevel.Messages,
            "verbose"  => TraceServerLevel.Verbose,
            "off"      => TraceServerLevel.Off,
            null       => fallback,
            _          => fallback
        };

    private void UpdateFromToken(JToken token, string source)
    {
        // Only allow disableIndexOnInitialize to change during initialize
        bool newDisableIndex = DisableIndexOnInitialize;
        if (source == "initialize")
        {
            newDisableIndex =
                token["disableIndexOnInitialize"]?.Value<bool?>()
                ?? token["gscode"]?["disableIndexOnInitialize"]?.Value<bool?>()
                ?? DisableIndexOnInitialize;
        }

        string? traceRaw =
            token["trace.server"]?.Value<string?>()
            ?? token["trace"]?["server"]?.Value<string?>()
            ?? token["gscode"]?["trace.server"]?.Value<string?>()
            ?? token["gscode"]?["trace"]?["server"]?.Value<string?>();

        TraceServerLevel traceLevel = ParseTraceServer(traceRaw, TraceServer);

        bool anyChange = false;
        lock (_gate)
        {
            if (source == "initialize" && newDisableIndex != DisableIndexOnInitialize)
            {
                DisableIndexOnInitialize = newDisableIndex;
                anyChange = true;
            }
            if (traceLevel != TraceServer)
            {
                TraceServer = traceLevel;
                anyChange = true;
            }
        }

        if (anyChange)
        {
            _logger.LogDebug(
                "Config updated ({Source}): disableIndex={Disable} (init-only), traceServer={Trace}",
                source, DisableIndexOnInitialize, TraceServer);
            Changed?.Invoke(this);
        }
    }
}