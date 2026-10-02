using GSCode.Workspace.Indexing;
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
        public bool FormatIndentCaseLabels { get; init; } = true;
        public bool FormatIndentDevBlocks { get; init; }
        public bool FormatFixCasing { get; init; } = true;
        public int FormatAlignMaxPadding { get; init; } = 20;
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

    /// <summary>
    /// <see cref="WorkspaceIndexingMode"/> read once, in one place. Anything unrecognised is
    /// <c>partial</c>, the default. Startup, the folder handler, and every path that has to keep a
    /// closed file's cross-file diagnostics in <c>full</c> mode ask this rather than comparing the
    /// string themselves.
    /// </summary>
    public IndexingMode IndexingMode
    {
        get
        {
            string mode = _current.WorkspaceIndexingMode;
            if ( string.Equals(mode, "off", StringComparison.OrdinalIgnoreCase) )
            {
                return IndexingMode.Off;
            }

            if ( string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) )
            {
                return IndexingMode.Full;
            }

            return IndexingMode.Partial;
        }
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

    /// <summary>Whether `case` labels sit one level inside their `switch`.</summary>
    public bool FormatIndentCaseLabels
    {
        get { return _current.FormatIndentCaseLabels; }
        set { _current = _current with { FormatIndentCaseLabels = value }; }
    }

    /// <summary>Whether the body of a `/# … #/` dev block is indented one level.</summary>
    public bool FormatIndentDevBlocks
    {
        get { return _current.FormatIndentDevBlocks; }
        set { _current = _current with { FormatIndentDevBlocks = value }; }
    }

    /// <summary>Whether formatting lowercases keywords and gives calls their function's spelling.</summary>
    public bool FormatFixCasing
    {
        get { return _current.FormatFixCasing; }
        set { _current = _current with { FormatFixCasing = value }; }
    }

    /// <summary>The most spaces consecutive alignment may add to one line; 0 for no limit.</summary>
    public int FormatAlignMaxPadding
    {
        get { return _current.FormatAlignMaxPadding; }
        set { _current = _current with { FormatAlignMaxPadding = value }; }
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
            RawEnabled = Flag(section, "raw", "enabled") ?? current.RawEnabled,
            RawPath = section.Value<string>("rawPath") ?? current.RawPath,
            ModsPath = section.Value<string>("modsPath") ?? current.ModsPath,
            RawFileWarningMode = section.Value<string>("rawFileWarningMode") ?? current.RawFileWarningMode,
            OutlineShowAssignments = Flag(section, "outline", "showAssignments") ?? current.OutlineShowAssignments,
            CodeLensEnabled = Flag(section, "codeLens", "enabled") ?? current.CodeLensEnabled,
            InlayParameterNames = Flag(section, "inlayHints", "parameterNames") ?? current.InlayParameterNames,
            InlayInferredTypes = Flag(section, "inlayHints", "inferredTypes") ?? current.InlayInferredTypes,
            InlayMacroParameterNames = Flag(section, "inlayHints", "macroParameterNames") ?? current.InlayMacroParameterNames,
            CompletionLiterals = Flag(section, "completion", "literals") ?? current.CompletionLiterals,
            CompletionAutoImport = Flag(section, "completion", "autoImport") ?? current.CompletionAutoImport,
            CompletionCallPunctuation = Text(section, "completion", "callPunctuation") ?? current.CompletionCallPunctuation,
            CompletionParameterHints = Flag(section, "completion", "parameterHints") ?? current.CompletionParameterHints,
            DiagnosticsScope = Text(section, "diagnostics", "scope") ?? current.DiagnosticsScope,
            FormatPadParens = Flag(section, "format", "padParens") ?? current.FormatPadParens,
            FormatPadCallParens = Flag(section, "format", "padCallParens") ?? current.FormatPadCallParens,
            FormatPadBrackets = Flag(section, "format", "padBrackets") ?? current.FormatPadBrackets,
            FormatSpaceBeforeControlParen = Flag(section, "format", "spaceBeforeControlParen") ?? current.FormatSpaceBeforeControlParen,

            // Clamped, not trusted: the formatter emits this many blank lines, and a negative one
            // from a hand-edited settings file is a knob nobody meant to have.
            FormatMaxBlankLines = Math.Max(0, Number(section, "format", "maxBlankLines") ?? current.FormatMaxBlankLines),
            FormatAlignConsecutive = Flag(section, "format", "alignConsecutive") ?? current.FormatAlignConsecutive,
            FormatSortDirectives = Flag(section, "format", "sortDirectives") ?? current.FormatSortDirectives,
            FormatIndentCaseLabels = Flag(section, "format", "indentCaseLabels") ?? current.FormatIndentCaseLabels,
            FormatIndentDevBlocks = Flag(section, "format", "indentDevBlocks") ?? current.FormatIndentDevBlocks,
            FormatFixCasing = Flag(section, "format", "fixCasing") ?? current.FormatFixCasing,
            FormatAlignMaxPadding = Math.Max(0, Number(section, "format", "alignMaxPadding") ?? current.FormatAlignMaxPadding),
        };
    }

    /// <summary>
    /// A grouped setting, in either shape a payload carries it: the flat dotted key the client sends
    /// (<c>"format.padParens"</c>) or nested under its group. Null when the payload has neither.
    /// </summary>
    /// <remarks>
    /// One spelling per setting. Written out at each site, the key appeared twice, and a rename that
    /// reached one half and not the other would drop the setting to its default without a word.
    /// </remarks>
    private static bool? Flag(JToken section, string group, string name)
    {
        return section.Value<bool?>(group + "." + name) ?? section[group]?.Value<bool?>(name);
    }

    /// <summary>The number form of <see cref="Flag"/>.</summary>
    private static int? Number(JToken section, string group, string name)
    {
        return section.Value<int?>(group + "." + name) ?? section[group]?.Value<int?>(name);
    }

    /// <summary>The text form of <see cref="Flag"/>.</summary>
    private static string? Text(JToken section, string group, string name)
    {
        return section.Value<string>(group + "." + name) ?? section[group]?.Value<string>(name);
    }
}
