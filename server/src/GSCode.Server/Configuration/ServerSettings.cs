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
    public bool CodeLensEnabled { get; set; } = true;
    public bool InlayParameterNames { get; set; } = true;
    public bool InlayInferredTypes { get; set; } = true;
    public bool CompletionLiterals { get; set; } = true;

    /// <summary>"owner" (default) or "all" — how widely assignment-derived fields are offered.</summary>
    public string CompletionFieldScope { get; set; } = "owner";

    /// <summary>Whether control-flow parentheses are padded: `if ( x )` against `if (x)`.</summary>
    public bool FormatPadParens { get; set; } = true;

    /// <summary>The longest run of blank lines the formatter preserves.</summary>
    public int FormatMaxBlankLines { get; set; } = 2;

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
        CodeLensEnabled = section.Value<bool?>("codeLens.enabled")
            ?? section["codeLens"]?.Value<bool?>("enabled")
            ?? CodeLensEnabled;
        InlayParameterNames = section.Value<bool?>("inlayHints.parameterNames")
            ?? section["inlayHints"]?.Value<bool?>("parameterNames")
            ?? InlayParameterNames;
        InlayInferredTypes = section.Value<bool?>("inlayHints.inferredTypes")
            ?? section["inlayHints"]?.Value<bool?>("inferredTypes")
            ?? InlayInferredTypes;
        CompletionLiterals = section.Value<bool?>("completion.literals")
            ?? section["completion"]?.Value<bool?>("literals")
            ?? CompletionLiterals;
        CompletionFieldScope = section.Value<string>("completion.fieldScope")
            ?? section["completion"]?.Value<string>("fieldScope")
            ?? CompletionFieldScope;
        FormatPadParens = section.Value<bool?>("format.padParens")
            ?? section["format"]?.Value<bool?>("padParens")
            ?? FormatPadParens;
        FormatMaxBlankLines = section.Value<int?>("format.maxBlankLines")
            ?? section["format"]?.Value<int?>("maxBlankLines")
            ?? FormatMaxBlankLines;
    }
}
