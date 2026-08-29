namespace GSCode.Server.Formatting;

/// <summary>
/// The knobs the formatter honours: the editor's indentation settings, which arrive per request
/// in the LSP payload, plus the GSC-specific ones from configuration.
///
/// The defaults here are only a fallback for callers with no editor to ask — real requests always
/// carry tabSize and insertSpaces, because the editor resolves those per document. The language's
/// own convention is tabs (247,613 tab-led indented lines across the stock scripts against 886
/// space-led), but that belongs in the client's configurationDefaults where a user can override
/// it, not baked in here where they cannot.
/// </summary>
/// <param name="IndentWidth">
/// Columns per level, from the LSP <c>tabSize</c>. Only used when indenting with spaces; a tab is
/// one character whatever width the editor renders it at.
/// </param>
/// <param name="UseTabs">
/// Whether to indent with tabs — the LSP <c>insertSpaces</c>, inverted. False here so the
/// no-options fallback stays what it has always been; the client defaults the GSC languages to
/// tabs, which is what real requests then carry.
/// </param>
/// <param name="PadParens">
/// Whether control-flow parentheses are padded: <c>if ( x )</c> against <c>if (x)</c>. Both occur
/// in stock code (33,140 padded, 4,333 tight), so this is a genuine preference rather than a
/// convention with one right answer.
/// </param>
/// <param name="PadCallParens">
/// Whether a call's or declaration's parentheses are padded: <c>foo( a )</c> against <c>foo(a)</c>.
/// Separate from <see cref="PadParens"/> because the two are mixed freely in stock code, and the
/// tight-call, padded-condition combination is a common hand-written style.
/// </param>
/// <param name="PadBrackets">
/// Whether subscript and array-literal brackets are padded: <c>a[ i ]</c> against <c>a[i]</c>.
/// Adjacent brackets (<c>[[ ptr ]]</c>, <c>[]</c>) stay tight either way. Stock leans tight
/// (19,175 to 4,686); padded is the default to match the parentheses, but it is a preference.
/// </param>
/// <param name="MaxBlankLines">
/// The longest run of blank lines to preserve. Two by default, which keeps the 2,477 double blanks
/// in the stock scripts while still collapsing the 152 longer runs.
/// </param>
/// <param name="SortDirectives">
/// Whether the leading directive block is grouped and sorted. The formatter's only operation that
/// moves code rather than whitespace, so it carries its own safety checks and refuses the one
/// arrangement it could change the meaning of. On by default: 498 of the 980 stock scripts are not
/// in canonical order and the same 498 have unsorted <c>#using</c> lines, so the tidying is real.
/// </param>
/// <param name="AlignConsecutive">
/// Whether a run of consecutive assignments has its operators aligned. A deliberate override of the
/// stock scripts, which align almost nothing, so it is OFF here — the code-level default used by
/// tests and the corpus gates, which must keep measuring the unaligned baseline. The client setting
/// defaults it on, the same split already used for <see cref="UseTabs"/>: the code default is the
/// conservative one, the editor default is the intended one.
/// </param>
public readonly record struct FormatOptions(
    int IndentWidth = 4,
    bool UseTabs = false,
    bool PadParens = true,
    bool PadCallParens = true,
    bool PadBrackets = true,
    int MaxBlankLines = 2,
    bool SortDirectives = true,
    bool AlignConsecutive = false)
{
    /// <summary>
    /// The defaults, for callers with no editor settings to hand (tests, corpus gates).
    ///
    /// Spelled out rather than written <c>new()</c>. A struct always has an implicit parameterless
    /// constructor that zeroes every field, and it wins over the primary constructor's parameter
    /// defaults — so <c>new()</c> here would silently mean zero indent, no paren padding and no
    /// blank lines, not the values declared above.
    /// </summary>
    public static FormatOptions Default { get; } = new(
        IndentWidth: 4, UseTabs: false, PadParens: true, PadCallParens: true, PadBrackets: true, MaxBlankLines: 2,
        SortDirectives: true, AlignConsecutive: false);

    /// <summary>One level of indentation as text.</summary>
    public string IndentUnit
    {
        get { return UseTabs ? "\t" : new string(' ', Math.Max(1, IndentWidth)); }
    }

    /// <summary>
    /// The options for a WHOLE-document format: the editor's own indentation settings, which arrive
    /// per request, plus the GSC knobs from configuration.
    /// </summary>
    /// <remarks>
    /// The two fragment formatters — range and on-type — differ from this by one or two flags each
    /// and explain themselves at their own call sites, so they start here and use <c>with</c> rather
    /// than restating the four lines all three agree on. Those four were written out three times,
    /// which is three places to remember when the editor gains a setting.
    /// </remarks>
    public static FormatOptions From(int tabSize, bool insertSpaces, Configuration.ServerSettings settings)
    {
        return new FormatOptions(
            IndentWidth: tabSize > 0 ? tabSize : 4,
            UseTabs: !insertSpaces,
            PadParens: settings.FormatPadParens,
            PadCallParens: settings.FormatPadCallParens,
            PadBrackets: settings.FormatPadBrackets,
            MaxBlankLines: Math.Max(0, settings.FormatMaxBlankLines),
            SortDirectives: settings.FormatSortDirectives,
            AlignConsecutive: settings.FormatAlignConsecutive);
    }
}
