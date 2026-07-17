using Serilog.Events;

namespace GSCode.Server.Logging;

/// <summary>
/// Maps the client's gscode.serverLogLevel setting (off/error/warning/info/verbose)
/// onto the Serilog level switch that gates the whole server log channel.
/// </summary>
public static class ServerLogLevel
{
    // One past Fatal: no event can reach it, so the channel is truly silent.
    private static readonly LogEventLevel s_silenced = LogEventLevel.Fatal + 1;

    /// <summary>
    /// Converts a setting string into the switch level. Unknown values fall back to "info".
    /// </summary>
    public static LogEventLevel FromSetting(string? settingValue)
    {
        switch ( settingValue?.ToLowerInvariant() )
        {
            case "off":
                return s_silenced;
            case "error":
                return LogEventLevel.Error;
            case "warning":
                return LogEventLevel.Warning;
            case "verbose":
                return LogEventLevel.Verbose;
            case "info":
            default:
                return LogEventLevel.Information;
        }
    }
}
