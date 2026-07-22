using GSCode.Server.Configuration;
using GSCode.Server.Logging;
using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using Serilog.Core;

namespace GSCode.Server.Handlers;

/// <summary>Applies live gscode.* settings pushes (log level applies immediately, no restart).</summary>
public sealed class ConfigurationHandler : DidChangeConfigurationHandlerBase
{
    private readonly ServerSettings _settings;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly WorkspaceDiagnosticsPublisher _workspaceDiagnostics;

    public ConfigurationHandler(
        ServerSettings settings,
        LoggingLevelSwitch levelSwitch,
        WorkspaceDiagnosticsPublisher workspaceDiagnostics)
    {
        _settings = settings;
        _levelSwitch = levelSwitch;
        _workspaceDiagnostics = workspaceDiagnostics;
    }

    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        if ( request.Settings is not JToken settingsRoot )
        {
            return Unit.Task;
        }

        string previousScope = _settings.DiagnosticsScope;
        string previousSummary = _settings.EffectiveSummary;

        _settings.Apply(settingsRoot);
        _levelSwitch.MinimumLevel = ServerLogLevel.FromSetting(_settings.ServerLogLevel);

        // Only when something that matters actually moved. Clients push their whole configuration
        // on any settings edit, so logging unconditionally would write this line every time the
        // user changed a font size. Knowing a setting changed mid-session answers the other half
        // of "why is it doing that" — the half a startup line cannot.
        if ( !string.Equals(previousSummary, _settings.EffectiveSummary, StringComparison.Ordinal) )
        {
            Log.Information("Settings changed: {Settings}", _settings.EffectiveSummary);
        }

        // Only on an actual change: a republish walks every record, and clients push their whole
        // configuration on any settings edit, most of which has nothing to do with us.
        if ( !string.Equals(previousScope, _settings.DiagnosticsScope, StringComparison.Ordinal) )
        {
            _workspaceDiagnostics.Refresh();
        }

        return Unit.Task;
    }
}
