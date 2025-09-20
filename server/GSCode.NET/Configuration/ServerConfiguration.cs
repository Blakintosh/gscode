using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using GSCode.Parser.Data; // For updating completion behavior

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

    /// <summary>
    /// If true, the language server skips the initial workspace indexing pass during the LSP
    /// initialize phase. This reduces startup cost (useful for large workspaces or ephemeral runs),
    /// but delays population of features that depend on a full symbol scan until explicitly triggered.
    /// This value is only honored at initialization time; changing it later has no effect until restart.
    /// </summary>
    public bool DisableIndexOnInitialize { get; private set; }

    /// <summary>
    /// Controls server trace verbosity (independent of standard logging):
    /// Off = no protocol trace, Messages = high-level protocol events, Verbose = detailed/diagnostic.
    /// Can be updated dynamically via workspace configuration changes.
    /// </summary>
    public TraceServerLevel TraceServer { get; private set; } = TraceServerLevel.Off;

    /// <summary>
    /// Controls whether function completion items include parameter snippet placeholders.
    /// True (default) inserts parameters with tab stops. False inserts only the function name.
    /// VS Code setting name suggestion: gscode.enableFunctionCompletionParameterFilling
    /// </summary>
    public bool EnableFunctionCompletionParameterFilling { get; private set; } = true;

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

        bool newEnableParamFill =
            token["enableFunctionCompletionParameterFilling"]?.Value<bool?>()
            ?? token["gscode"]?["enableFunctionCompletionParameterFilling"]?.Value<bool?>()
            ?? EnableFunctionCompletionParameterFilling;

        bool anyChange = false;
        bool paramFillChanged = false;

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
            if (newEnableParamFill != EnableFunctionCompletionParameterFilling)
            {
                EnableFunctionCompletionParameterFilling = newEnableParamFill;
                anyChange = true;
                paramFillChanged = true;
            }
        }

        if (paramFillChanged)
        {
            DocumentCompletionsLibrary.ParameterFillResolver = () => EnableFunctionCompletionParameterFilling;
        }

        if (anyChange)
        {
            _logger.LogDebug(
                "Config updated ({Source}): disableIndex={Disable} (init-only), traceServer={Trace}, enableParamFill={ParamFill}",
                source, DisableIndexOnInitialize, TraceServer, EnableFunctionCompletionParameterFilling);
            Changed?.Invoke(this);
        }
    }
}