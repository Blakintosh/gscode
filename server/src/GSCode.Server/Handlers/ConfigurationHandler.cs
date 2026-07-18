using GSCode.Server.Configuration;
using GSCode.Server.Logging;
using MediatR;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog.Core;

namespace GSCode.Server.Handlers;

/// <summary>Applies live gscode.* settings pushes (log level applies immediately, no restart).</summary>
public sealed class ConfigurationHandler : DidChangeConfigurationHandlerBase
{
    private readonly ServerSettings _settings;
    private readonly LoggingLevelSwitch _levelSwitch;

    public ConfigurationHandler(ServerSettings settings, LoggingLevelSwitch levelSwitch)
    {
        _settings = settings;
        _levelSwitch = levelSwitch;
    }

    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        if ( request.Settings is JToken settingsRoot )
        {
            _settings.Apply(settingsRoot);
            _levelSwitch.MinimumLevel = ServerLogLevel.FromSetting(_settings.ServerLogLevel);
        }

        return Unit.Task;
    }
}
