using Newtonsoft.Json.Linq;
using Serilog;

namespace GSCode.Server.Configuration;

/// <summary>
/// The server-side view of the gscode.* client settings, parsed once from
/// initializationOptions and refreshed on didChangeConfiguration. One mutable
/// singleton — handlers read current values, writes happen on config pushes only.
///
/// The VALUES live in an immutable snapshot swapped by one reference write, and that is the whole
/// point of the indirection: assigned field by field while handler threads read them, a reader
/// could see a new Game beside an old RawPath — a configuration that was never sent — and a payload
/// throwing part-way would leave the settings half-applied. A failed Apply changes nothing at all,
/// because the new snapshot is published only once it is complete.
/// </summary>
public sealed class ServerSettings
{
    /// <summary>Every setting as one immutable value, so a reader always sees a coherent set.</summary>
    private sealed record Values
    {
        public string ServerLogLevel { get; init; } = "warning";
        public string WorkspaceIndexingMode { get; init; } = "partial";
        public bool EnableWorkspaceCache { get; init; } = true;
        public bool RawEnabled { get; init; } = true;
        public string RawPath { get; init; } = "";
        public string ModsPath { get; init; } = "";
        public string RawFileWarningMode { get; init; } = "stock";
        public bool OutlineShowAssignments { get; init; } = true;
        public bool CodeLensEnabled { get; init; }
        public bool InlayParameterNames { get; init; } = true;
        public bool InlayInferredTypes { get; init; } = true;
        public bool InlayMacroParameterNames { get; init; }
        public bool CompletionLiterals { get; init; } = true;
        public bool CompletionAutoImport { get; init; } = true;
        public string CompletionFieldScope { get; init; } = "owner";
        public string CompletionCallPunctuation { get; init; } = "parensAndSemicolon";
        public bool CompletionParameterHints { get; init; } = true;
        public string DiagnosticsScope { get; init; } = "workspace";
        public bool FormatPadParens { get; init; } = true;
        public bool FormatPadCallParens { get; init; } = true;
        public bool FormatPadBrackets { get; init; } = true;
        public bool FormatSpaceBeforeControlParen { get; init; } = true;
        public int FormatMaxBlankLines { get; init; } = 2;
        public bool FormatSortDirectives { get; init; } = true;
        public bool FormatAlignConsecutive { get; init; } = true;
        public string Game { get; init; } = "bo3";
    }

    /// <summary>
    /// Volatile so a handler thread is guaranteed to see a swap rather than a cached reference.
    /// </summary>
    private volatile Values _current = new();

    public string ServerLogLevel
    {
        get { return _current.ServerLogLevel; }
        set { _current = _current with { ServerLogLevel = value }; }
    }

    public string WorkspaceIndexingMode
    {
        get { return _current.WorkspaceIndexingMode; }
        set { _current = _current with { WorkspaceIndexingMode = value }; }
    }

    public bool EnableWorkspaceCache
    {
        get { return _current.EnableWorkspaceCache; }
        set { _current = _current with { EnableWorkspaceCache = value }; }
    }

    public bool RawEnabled
    {
        get { return _current.RawEnabled; }
        set { _current = _current with { RawEnabled = value }; }
    }

    public string RawPath
    {
        get { return _current.RawPath; }
        set { _current = _current with { RawPath = value }; }
    }

    public string ModsPath
    {
        get { return _current.ModsPath; }
        set { _current = _current with { ModsPath = value }; }
    }

    public string RawFileWarningMode
    {
        get { return _current.RawFileWarningMode; }
        set { _current = _current with { RawFileWarningMode = value }; }
    }

    public bool OutlineShowAssignments
    {
        get { return _current.OutlineShowAssignments; }
        set { _current = _current with { OutlineShowAssignments = value }; }
    }

    public bool CodeLensEnabled
    {
        get { return _current.CodeLensEnabled; }
        set { _current = _current with { CodeLensEnabled = value }; }
    }

    public bool InlayParameterNames
    {
        get { return _current.InlayParameterNames; }
        set { _current = _current with { InlayParameterNames = value }; }
    }

    public bool InlayInferredTypes
    {
        get { return _current.InlayInferredTypes; }
        set { _current = _current with { InlayInferredTypes = value }; }
    }

    /// <summary>
    /// Whether a MACRO invocation's arguments get their parameter names —
    /// <c>IS_TRUE( __a: level.ready )</c>. Off by default: macro parameters are named for the
    /// macro's own body rather than for the caller, so the label often adds noise rather than
    /// meaning, and macros are dense in this code.
    /// </summary>
    public bool InlayMacroParameterNames
    {
        get { return _current.InlayMacroParameterNames; }
        set { _current = _current with { InlayMacroParameterNames = value }; }
    }

    public bool CompletionLiterals
    {
        get { return _current.CompletionLiterals; }
        set { _current = _current with { CompletionLiterals = value }; }
    }

    /// <summary>
    /// Whether completion offers functions from files this one has not imported, inserting the
    /// directive with them (the gscode.completion.autoImport setting).
    /// </summary>
    public bool CompletionAutoImport
    {
        get { return _current.CompletionAutoImport; }
        set { _current = _current with { CompletionAutoImport = value }; }
    }

    /// <summary>"owner" (default) or "all" — how widely assignment-derived fields are offered.</summary>
    public string CompletionFieldScope
    {
        get { return _current.CompletionFieldScope; }
        set { _current = _current with { CompletionFieldScope = value }; }
    }

    /// <summary>
    /// How much punctuation a completed call brings with it: "off", "parens", or
    /// "parensAndSemicolon" (the default).
    /// </summary>
    public string CompletionCallPunctuation
    {
        get { return _current.CompletionCallPunctuation; }
        set { _current = _current with { CompletionCallPunctuation = value }; }
    }

    /// <summary>
    /// Whether a function's parameter names appear inline in its completion label —
    /// <c>get_players( team )</c> rather than <c>get_players</c>.
    ///
    /// On by default: the parameters are already in hand when the list is built, and in this
    /// codebase they are frequently the only thing telling two entries apart. Off restores the bare
    /// names for anyone who finds the rows noisy.
    /// </summary>
    public bool CompletionParameterHints
    {
        get { return _current.CompletionParameterHints; }
        set { _current = _current with { CompletionParameterHints = value }; }
    }

    /// <summary>
    /// Which files get diagnostics published: "open" (only what is open), "workspace" (every
    /// indexed file of your own, the default) or "all" (including the stock scripts).
    /// </summary>
    public string DiagnosticsScope
    {
        get { return _current.DiagnosticsScope; }
        set { _current = _current with { DiagnosticsScope = value }; }
    }

    /// <summary>Whether control-flow parentheses are padded: `if ( x )` against `if (x)`.</summary>
    public bool FormatPadParens
    {
        get { return _current.FormatPadParens; }
        set { _current = _current with { FormatPadParens = value }; }
    }

    /// <summary>Whether call parentheses are padded: `foo( a )` against `foo(a)`.</summary>
    public bool FormatPadCallParens
    {
        get { return _current.FormatPadCallParens; }
        set { _current = _current with { FormatPadCallParens = value }; }
    }

    /// <summary>Whether subscript brackets are padded: `a[ i ]` against `a[i]`.</summary>
    public bool FormatPadBrackets
    {
        get { return _current.FormatPadBrackets; }
        set { _current = _current with { FormatPadBrackets = value }; }
    }

    /// <summary>Whether a control-flow keyword is spaced from its parenthesis: `if (` against `if(`.</summary>
    public bool FormatSpaceBeforeControlParen
    {
        get { return _current.FormatSpaceBeforeControlParen; }
        set { _current = _current with { FormatSpaceBeforeControlParen = value }; }
    }

    /// <summary>The longest run of blank lines the formatter preserves.</summary>
    public int FormatMaxBlankLines
    {
        get { return _current.FormatMaxBlankLines; }
        set { _current = _current with { FormatMaxBlankLines = value }; }
    }

    /// <summary>Whether Format Document groups and sorts the leading directive block.</summary>
    public bool FormatSortDirectives
    {
        get { return _current.FormatSortDirectives; }
        set { _current = _current with { FormatSortDirectives = value }; }
    }

    /// <summary>Whether Format Document aligns the operators of consecutive assignments.</summary>
    public bool FormatAlignConsecutive
    {
        get { return _current.FormatAlignConsecutive; }
        set { _current = _current with { FormatAlignConsecutive = value }; }
    }

    /// <summary>The game whose dialect the workspace targets, by short name (e.g. "bo3", "cod4").</summary>
    public string Game
    {
        get { return _current.Game; }
        set { _current = _current with { Game = value }; }
    }

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
            Values values = _current;
            return $"game={values.Game}, indexing={values.WorkspaceIndexingMode}, cache={OnOff(values.EnableWorkspaceCache)}, "
                + $"diagnostics={values.DiagnosticsScope}, raw={OnOff(values.RawEnabled)}, "
                + $"rawWarning={values.RawFileWarningMode}, codeLens={OnOff(values.CodeLensEnabled)}, "
                + $"log={values.ServerLogLevel}";
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
            Values values = _current;
            return $"types={OnOff(values.InlayInferredTypes)}, parameters={OnOff(values.InlayParameterNames)}, "
                + $"macroParameters={OnOff(values.InlayMacroParameterNames)}";
        }
    }

    private static string OnOff(bool value)
    {
        return value ? "on" : "off";
    }

    /// <summary>
    /// Applies a gscode settings payload; missing keys keep their current values.
    ///
    /// Built into a new snapshot and published in one write, so no reader ever sees a partial
    /// configuration. A malformed value — Newtonsoft throws converting a string to an int — is
    /// logged and the whole payload dropped rather than half-kept, which also stops a bad payload
    /// failing the initialize handshake it arrives on.
    /// </summary>
    public void Apply(JToken settingsRoot)
    {
        JToken? section = settingsRoot["gscode"];
        if ( section is null )
        {
            return;
        }

        try
        {
            _current = Build(_current, section);
        }
        catch ( Exception exception )
        {
            Log.Warning(exception, "Ignored an unreadable gscode settings payload; keeping the current values");
        }
    }

    private static Values Build(Values current, JToken section)
    {
        return new Values
        {
            Game = section.Value<string>("game") ?? current.Game,
            ServerLogLevel = section.Value<string>("serverLogLevel") ?? current.ServerLogLevel,
            WorkspaceIndexingMode = section.Value<string>("workspaceIndexingMode") ?? current.WorkspaceIndexingMode,
            EnableWorkspaceCache = section.Value<bool?>("enableWorkspaceCache") ?? current.EnableWorkspaceCache,
            RawEnabled = section.Value<bool?>("raw.enabled")
                ?? section["raw"]?.Value<bool?>("enabled")
                ?? current.RawEnabled,
            RawPath = section.Value<string>("rawPath") ?? current.RawPath,
            ModsPath = section.Value<string>("modsPath") ?? current.ModsPath,
            RawFileWarningMode = section.Value<string>("rawFileWarningMode") ?? current.RawFileWarningMode,
            OutlineShowAssignments = section.Value<bool?>("outline.showAssignments")
                ?? section["outline"]?.Value<bool?>("showAssignments")
                ?? current.OutlineShowAssignments,
            CodeLensEnabled = section.Value<bool?>("codeLens.enabled")
                ?? section["codeLens"]?.Value<bool?>("enabled")
                ?? current.CodeLensEnabled,
            InlayParameterNames = section.Value<bool?>("inlayHints.parameterNames")
                ?? section["inlayHints"]?.Value<bool?>("parameterNames")
                ?? current.InlayParameterNames,
            InlayInferredTypes = section.Value<bool?>("inlayHints.inferredTypes")
                ?? section["inlayHints"]?.Value<bool?>("inferredTypes")
                ?? current.InlayInferredTypes,
            InlayMacroParameterNames = section.Value<bool?>("inlayHints.macroParameterNames")
                ?? section["inlayHints"]?.Value<bool?>("macroParameterNames")
                ?? current.InlayMacroParameterNames,
            CompletionLiterals = section.Value<bool?>("completion.literals")
                ?? section["completion"]?.Value<bool?>("literals")
                ?? current.CompletionLiterals,
            CompletionAutoImport = section.Value<bool?>("completion.autoImport")
                ?? section["completion"]?.Value<bool?>("autoImport")
                ?? current.CompletionAutoImport,
            CompletionFieldScope = section.Value<string>("completion.fieldScope")
                ?? section["completion"]?.Value<string>("fieldScope")
                ?? current.CompletionFieldScope,
            CompletionCallPunctuation = section.Value<string>("completion.callPunctuation")
                ?? section["completion"]?.Value<string>("callPunctuation")
                ?? current.CompletionCallPunctuation,
            CompletionParameterHints = section.Value<bool?>("completion.parameterHints")
                ?? section["completion"]?.Value<bool?>("parameterHints")
                ?? current.CompletionParameterHints,
            DiagnosticsScope = section.Value<string>("diagnostics.scope")
                ?? section["diagnostics"]?.Value<string>("scope")
                ?? current.DiagnosticsScope,
            FormatPadParens = section.Value<bool?>("format.padParens")
                ?? section["format"]?.Value<bool?>("padParens")
                ?? current.FormatPadParens,
            FormatPadCallParens = section.Value<bool?>("format.padCallParens")
                ?? section["format"]?.Value<bool?>("padCallParens")
                ?? current.FormatPadCallParens,
            FormatPadBrackets = section.Value<bool?>("format.padBrackets")
                ?? section["format"]?.Value<bool?>("padBrackets")
                ?? current.FormatPadBrackets,
            FormatSpaceBeforeControlParen = section.Value<bool?>("format.spaceBeforeControlParen")
                ?? section["format"]?.Value<bool?>("spaceBeforeControlParen")
                ?? current.FormatSpaceBeforeControlParen,

            // Clamped, not trusted: the formatter emits this many blank lines, and a negative one
            // from a hand-edited settings file is a knob nobody meant to have.
            FormatMaxBlankLines = Math.Max(
                0,
                section.Value<int?>("format.maxBlankLines")
                    ?? section["format"]?.Value<int?>("maxBlankLines")
                    ?? current.FormatMaxBlankLines),
            FormatAlignConsecutive = section.Value<bool?>("format.alignConsecutive")
                ?? section["format"]?.Value<bool?>("alignConsecutive")
                ?? current.FormatAlignConsecutive,
            FormatSortDirectives = section.Value<bool?>("format.sortDirectives")
                ?? section["format"]?.Value<bool?>("sortDirectives")
                ?? current.FormatSortDirectives,
        };
    }
}
