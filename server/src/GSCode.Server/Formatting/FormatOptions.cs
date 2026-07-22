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
public readonly record struct FormatOptions(
    int IndentWidth = 4,
    bool UseTabs = false,
    bool PadParens = true,
    int MaxBlankLines = 2,
    bool SortDirectives = true)
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
        IndentWidth: 4, UseTabs: false, PadParens: true, MaxBlankLines: 2, SortDirectives: true);

    /// <summary>One level of indentation as text.</summary>
    public string IndentUnit
    {
        get { return UseTabs ? "\t" : new string(' ', Math.Max(1, IndentWidth)); }
    }
}
