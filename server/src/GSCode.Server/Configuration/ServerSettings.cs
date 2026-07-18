using Newtonsoft.Json.Linq;

namespace GSCode.Server.Configuration;

/// <summary>
/// The server-side view of the gscode.* client settings, parsed once from
/// initializationOptions and refreshed on didChangeConfiguration. One mutable
/// singleton — handlers read current values, writes happen on config pushes only.
/// </summary>
public sealed class ServerSettings
{
    public string ServerLogLevel { get; set; } = "off";
    public string WorkspaceIndexingMode { get; set; } = "partial";
    public bool EnableWorkspaceCache { get; set; } = true;
    public bool RawEnabled { get; set; } = true;
    public string RawPathOverride { get; set; } = "";
    public string ModsPathOverride { get; set; } = "";
    public string RawFileWarningMode { get; set; } = "stock";
    public bool OutlineShowAssignments { get; set; } = true;

    /// <summary>Applies a { "gscode": { ... } } payload; missing keys keep their current values.</summary>
    public void Apply(JToken settingsRoot)
    {
        JToken? section = settingsRoot["gscode"];
        if ( section is null )
        {
            return;
        }

        ServerLogLevel = section.Value<string>("serverLogLevel") ?? ServerLogLevel;
        WorkspaceIndexingMode = section.Value<string>("workspaceIndexingMode") ?? WorkspaceIndexingMode;
        EnableWorkspaceCache = section.Value<bool?>("enableWorkspaceCache") ?? EnableWorkspaceCache;
        RawEnabled = section.Value<bool?>("raw.enabled") ?? section["raw"]?.Value<bool?>("enabled") ?? RawEnabled;
        RawPathOverride = section.Value<string>("rawPath") ?? RawPathOverride;
        ModsPathOverride = section.Value<string>("modsPath") ?? ModsPathOverride;
        RawFileWarningMode = section.Value<string>("rawFileWarningMode") ?? RawFileWarningMode;
        OutlineShowAssignments = section.Value<bool?>("outline.showAssignments")
            ?? section["outline"]?.Value<bool?>("showAssignments")
            ?? OutlineShowAssignments;
    }
}
