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
    public string RawPath { get; set; } = "";
    public string ModsPath { get; set; } = "";
    public string RawFileWarningMode { get; set; } = "stock";
    public bool OutlineShowAssignments { get; set; } = true;
    public bool CodeLensEnabled { get; set; } = true;
    public bool InlayParameterNames { get; set; } = true;
    public bool InlayInferredTypes { get; set; } = true;
    public bool CompletionLiterals { get; set; } = true;

    /// <summary>"owner" (default) or "all" — how widely assignment-derived fields are offered.</summary>
    public string CompletionFieldScope { get; set; } = "owner";

    /// <summary>
    /// How much punctuation a completed call brings with it: "off", "parens", or
    /// "parensAndSemicolon" (the default).
    /// </summary>
    public string CompletionCallPunctuation { get; set; } = "parensAndSemicolon";

    /// <summary>
    /// Which files get diagnostics published: "open" (only what is open), "workspace" (every
    /// indexed file of your own, the default) or "all" (including the stock scripts).
    /// </summary>
    public string DiagnosticsScope { get; set; } = "workspace";

    /// <summary>Whether control-flow parentheses are padded: `if ( x )` against `if (x)`.</summary>
    public bool FormatPadParens { get; set; } = true;

    /// <summary>The longest run of blank lines the formatter preserves.</summary>
    public int FormatMaxBlankLines { get; set; } = 2;

    /// <summary>Whether Format Document groups and sorts the leading directive block.</summary>
    public bool FormatSortDirectives { get; set; } = true;

    /// <summary>Whether Format Document aligns the operators of consecutive assignments.</summary>
    public bool FormatAlignConsecutive { get; set; } = true;

    /// <summary>The game whose dialect the workspace targets, by short name (e.g. "bo3", "cod4").</summary>
    public string Game { get; set; } = "bo3";

    /// <summary>
    /// The settings that actually change what the server does, as one line.
    ///
    /// Logged at startup and again whenever it changes, because nearly every "why is it doing
    /// that" turns out to be one of these — indexing off, the cache serving a stale record,
    /// diagnostics scoped to open files, raw resolution disabled — and none is visible from the
    /// symptom alone. Being a single string also makes "did anything meaningful change" a string
    /// comparison rather than a field-by-field diff.
    ///
    /// Deliberately not every setting: a dump of all of them is one nobody reads.
    /// </summary>
    public string EffectiveSummary
    {
        get
        {
            return $"game={Game}, indexing={WorkspaceIndexingMode}, cache={OnOff(EnableWorkspaceCache)}, "
                + $"diagnostics={DiagnosticsScope}, raw={OnOff(RawEnabled)}, "
                + $"rawWarning={RawFileWarningMode}, codeLens={OnOff(CodeLensEnabled)}, "
                + $"log={ServerLogLevel}";
        }
    }

    private static string OnOff(bool value)
    {
        return value ? "on" : "off";
    }

    /// <summary>Applies a { "gscode": { ... } } payload; missing keys keep their current values.</summary>
    public void Apply(JToken settingsRoot)
    {
        JToken? section = settingsRoot["gscode"];
        if ( section is null )
        {
            return;
        }

        Game = section.Value<string>("game") ?? Game;
        ServerLogLevel = section.Value<string>("serverLogLevel") ?? ServerLogLevel;
        WorkspaceIndexingMode = section.Value<string>("workspaceIndexingMode") ?? WorkspaceIndexingMode;
        EnableWorkspaceCache = section.Value<bool?>("enableWorkspaceCache") ?? EnableWorkspaceCache;
        RawEnabled = section.Value<bool?>("raw.enabled") ?? section["raw"]?.Value<bool?>("enabled") ?? RawEnabled;
        RawPath = section.Value<string>("rawPath") ?? RawPath;
        ModsPath = section.Value<string>("modsPath") ?? ModsPath;
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
        CompletionCallPunctuation = section.Value<string>("completion.callPunctuation")
            ?? section["completion"]?.Value<string>("callPunctuation")
            ?? CompletionCallPunctuation;
        DiagnosticsScope = section.Value<string>("diagnostics.scope")
            ?? section["diagnostics"]?.Value<string>("scope")
            ?? DiagnosticsScope;
        FormatPadParens = section.Value<bool?>("format.padParens")
            ?? section["format"]?.Value<bool?>("padParens")
            ?? FormatPadParens;
        FormatMaxBlankLines = section.Value<int?>("format.maxBlankLines")
            ?? section["format"]?.Value<int?>("maxBlankLines")
            ?? FormatMaxBlankLines;
        FormatAlignConsecutive = section.Value<bool?>("format.alignConsecutive")
            ?? section["format"]?.Value<bool?>("alignConsecutive")
            ?? FormatAlignConsecutive;
        FormatSortDirectives = section.Value<bool?>("format.sortDirectives")
            ?? section["format"]?.Value<bool?>("sortDirectives")
            ?? FormatSortDirectives;
    }
}
