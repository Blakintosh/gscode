using Serilog.Core;
using Serilog.Events;

namespace GSCode.Server.Logging;

/// <summary>
/// Owns the level switch's startup-floor lifecycle end to end: what the client asked for, and
/// when the channel is allowed to drop to it.
///
/// <see cref="ServerLogLevel.StartupFloor"/> is the rule; this is the state the rule needs to be
/// applied more than once from different places without disagreeing with itself. Extracted so
/// <c>OnInitialize</c> (which learns the requested level, in <c>ServerServices</c>) and the startup
/// task's <c>finally</c> block (which settles it, in <c>StartupIndexRunner</c>) can share the state
/// without a <c>Program.cs</c> top-level local crossing into files that cannot see it.
/// </summary>
public sealed class StartupLogController
{
    private readonly LoggingLevelSwitch _switch;
    private LogEventLevel _requested = LogEventLevel.Information;

    public StartupLogController(LoggingLevelSwitch levelSwitch)
    {
        _switch = levelSwitch;
    }

    /// <summary>
    /// Records what the client asked for and applies the startup floor immediately — Information,
    /// unless the client asked for something more verbose or asked for silence outright. See
    /// <see cref="ServerLogLevel.StartupFloor"/> for why.
    /// </summary>
    public void SetRequested(LogEventLevel requested)
    {
        _requested = requested;
        _switch.MinimumLevel = ServerLogLevel.StartupFloor(requested);
    }

    /// <summary>Drops the channel to what was requested, now that startup has said its piece.</summary>
    public void Settle()
    {
        _switch.MinimumLevel = _requested;
    }
}
