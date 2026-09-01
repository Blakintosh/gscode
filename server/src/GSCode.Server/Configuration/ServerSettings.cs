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
    public bool CodeLensEnabled { get; set; } = false;
    public bool InlayParameterNames { get; set; } = true;
    public bool InlayInferredTypes { get; set; } = true;

    /// <summary>
    /// Whether a MACRO invocation's arguments get their parameter names —
    /// <c>IS_TRUE( __a: level.ready )</c>. Off by default: macro parameters are named for the
    /// macro's own body rather than for the caller, so the label often adds noise rather than
    /// meaning, and macros are dense in this code.
    /// </summary>
    public bool InlayMacroParameterNames { get; set; } = false;
    public bool CompletionLiterals { get; set; } = true;

    /// <summary>"owner" (default) or "all" — how widely assignment-derived fields are offered.</summary>
    public string CompletionFieldScope { get; set; } = "owner";

    /// <summary>
    /// How much punctuation a completed call brings with it: "off", "parens", or
    /// "parensAndSemicolon" (the default).
    /// </summary>
    public string CompletionCallPunctuation { get; set; } = "parensAndSemicolon";

    /// <summary>
    /// Whether a function's parameter names appear inline in its completion label —
    /// <c>get_players( team )</c> rather than <c>get_players</c>.
    ///
    /// On by default: the parameters are already in hand when the list is built, and in this
    /// codebase they are frequently the only thing telling two entries apart. Off restores the bare
    /// names for anyone who finds the rows noisy.
    /// </summary>
    public bool CompletionParameterHints { get; set; } = true;

    /// <summary>
    /// Which files get diagnostics published: "open" (only what is open), "workspace" (every
    /// indexed file of your own, the default) or "all" (including the stock scripts).
    /// </summary>
    public string DiagnosticsScope { get; set; } = "workspace";

    /// <summary>Whether control-flow parentheses are padded: `if ( x )` against `if (x)`.</summary>
    public bool FormatPadParens { get; set; } = true;

    /// <summary>Whether call parentheses are padded: `foo( a )` against `foo(a)`.</summary>
    public bool FormatPadCallParens { get; set; } = true;

    /// <summary>Whether subscript brackets are padded: `a[ i ]` against `a[i]`.</summary>
    public bool FormatPadBrackets { get; set; } = true;

    /// <summary>Whether a control-flow keyword is spaced from its parenthesis: `if (` against `if(`.</summary>
    public bool FormatSpaceBeforeControlParen { get; set; } = true;

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

    /// <summary>
    /// The three inlay families as one comparable value.
    ///
    /// A hint is computed per request and cached by the client, so toggling a family changes
    /// nothing the user can see until something else invalidates the document — a keystroke, a
    /// scroll, a reopen. Comparing this across a settings push is what tells the handler to ask
    /// for a refresh, and comparing a STRING rather than three fields keeps the caller to one
    /// line. Not part of <see cref="EffectiveSummary"/>: that line is for the log, and these
    /// three are not what a "why is it doing that" turns on.
    /// </summary>
    public string InlayFamilies
    {
        get
        {
            return $"types={OnOff(InlayInferredTypes)}, parameters={OnOff(InlayParameterNames)}, "
                + $"macroParameters={OnOff(InlayMacroParameterNames)}";
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
        InlayMacroParameterNames = section.Value<bool?>("inlayHints.macroParameterNames")
            ?? section["inlayHints"]?.Value<bool?>("macroParameterNames")
            ?? InlayMacroParameterNames;
        CompletionLiterals = section.Value<bool?>("completion.literals")
            ?? section["completion"]?.Value<bool?>("literals")
            ?? CompletionLiterals;
        CompletionFieldScope = section.Value<string>("completion.fieldScope")
            ?? section["completion"]?.Value<string>("fieldScope")
            ?? CompletionFieldScope;
        CompletionCallPunctuation = section.Value<string>("completion.callPunctuation")
            ?? section["completion"]?.Value<string>("callPunctuation")
            ?? CompletionCallPunctuation;
        CompletionParameterHints = section.Value<bool?>("completion.parameterHints")
            ?? section["completion"]?.Value<bool?>("parameterHints")
            ?? CompletionParameterHints;
        DiagnosticsScope = section.Value<string>("diagnostics.scope")
            ?? section["diagnostics"]?.Value<string>("scope")
            ?? DiagnosticsScope;
        FormatPadParens = section.Value<bool?>("format.padParens")
            ?? section["format"]?.Value<bool?>("padParens")
            ?? FormatPadParens;
        FormatPadCallParens = section.Value<bool?>("format.padCallParens")
            ?? section["format"]?.Value<bool?>("padCallParens")
            ?? FormatPadCallParens;
        FormatPadBrackets = section.Value<bool?>("format.padBrackets")
            ?? section["format"]?.Value<bool?>("padBrackets")
            ?? FormatPadBrackets;
        FormatSpaceBeforeControlParen = section.Value<bool?>("format.spaceBeforeControlParen")
            ?? section["format"]?.Value<bool?>("spaceBeforeControlParen")
            ?? FormatSpaceBeforeControlParen;
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
