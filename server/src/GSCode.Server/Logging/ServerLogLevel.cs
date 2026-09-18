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
    /// The level to run at while the server is starting up, given what the client asked for.
    ///
    /// The startup lines — the resolved roots, the effective settings, the indexing breakdown and
    /// how long the server took to be ready — are written at Information, and the client's default
    /// is "warning", so every one of them was being discarded in the shipped configuration. They
    /// exist precisely so that a user attaching a log to a bug report does not first have to be
    /// told to raise the level, which is advice that only helps if the problem happens twice.
    ///
    /// "off" is honoured immediately, because it is the one setting that asks for silence rather
    /// than for less detail. A level MORE verbose than Information is honoured immediately too —
    /// there is nothing to hold back.
    /// </summary>
    public static LogEventLevel StartupFloor(LogEventLevel requested)
    {
        if ( requested == s_silenced || requested < LogEventLevel.Information )
        {
            return requested;
        }

        return LogEventLevel.Information;
    }

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
