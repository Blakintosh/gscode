using System.Threading;
using System.Threading.Tasks;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Microsoft.Extensions.Logging;
using GSCode.NET.Configuration;

namespace GSCode.NET.LSP.Handlers;

internal sealed class ConfigurationDidChangeHandler : DidChangeConfigurationHandlerBase
{
    private readonly ServerConfiguration _config;
    private readonly ILogger<ConfigurationDidChangeHandler> _logger;

    public ConfigurationDidChangeHandler(ServerConfiguration config, ILogger<ConfigurationDidChangeHandler> logger)
    {
        _config = config;
        _logger = logger;
    }

    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("workspace/didChangeConfiguration received.");
        _config.ApplyDidChangeConfiguration(request.Settings);
        return Task.FromResult(Unit.Value);
    }
}

public static class ServerConfigurationExtensions
{
    public static ServerConfiguration WithConfigurationSection(this ServerConfiguration configuration, string sectionName)
    {
        // Implementation for adding a configuration section
        return configuration;
    }
}