using System.Collections.Immutable;
using System.Text.RegularExpressions;
using GSCode.Core.Diagnostics;
using GSCode.Parser.Lexing;

namespace GSCode.Parser.Extraction;

/// <summary>What a pragma turns off.</summary>
public enum PragmaTarget
{
    /// <summary>One diagnostic code.</summary>
    Code,

    /// <summary>Every diagnostic.</summary>
    AllCodes,

    /// <summary>The formatter, over the lines it spans.</summary>
    Format,
}

/// <summary>How far a directive reaches.</summary>
public enum PragmaScope
{
    /// <summary>From its line to the next directive that touches the same target, or end of file.</summary>
    FromHere,

    /// <summary>Its line alone. Reads and writes no running state.</summary>
    OneLine,
}

/// <summary>
/// One <c>disable</c> or <c>restore</c>, at the line it applies from — which for a
/// <see cref="PragmaScope.OneLine"/> directive is the line it covers, not the line it was
/// written on.
/// </summary>
public readonly record struct PragmaDirective(
    int Line,
    bool Disable,
    PragmaTarget Target,
    int Code,
    PragmaScope Scope = PragmaScope.FromHere);

/// <summary>
/// In-source pragmas, carried INSIDE COMMENTS.
///
///     // #pragma disable 5014
///     doSomethingTheLintDoesNotUnderstand();
///     // #pragma restore 5014
///
///     // #pragma disable format
///     matrix = [ 1, 0, 0,
///                0, 1, 0 ];
///     // #pragma restore format
///
/// A comment is not a choice of style here but the only place they can live: GSC's linker reads
/// the file itself, so anything outside a comment would have to be real syntax the language does
/// not have, and every script carrying one would stop compiling.
///
/// <c>disable</c>/<c>restore</c> is C#'s pair and is kept for the reason it was chosen there: each
/// word says which way it goes, which <c>on</c>/<c>off</c> stops doing once two of them are nested,
/// and <c>enable</c> would claim something this cannot do — there is no diagnostic that is off by
/// default and could be switched on.
///
/// <b>C#'s <c>warning</c> is NOT kept, because here it would be a lie.</b> Suppression is keyed on
/// the CODE alone: <see cref="IsSuppressed"/> takes a code and a line and never sees a
/// <see cref="DiagnosticSeverity"/>, and <c>WorkspaceLints.ApplyPragmas</c> runs it over the
/// combined set — the file's own parse diagnostics as well as the lints. So every severity is
/// suppressible, Errors and Hints alike, and every band with them: a 3xxx syntax error is turned
/// off by its code the same way a 5xxx hint is. C# cannot suppress a compiler error at all, so
/// borrowing its word would have imported the wrong expectation along with the familiarity.
///
/// The breadth is not a decision taken here — it follows from WHERE suppression is applied. One
/// filter over one merged list cannot branch on a severity it is not given. If a band should ever
/// become exempt, this is the summary that has to change with it.
///
/// The practical consequence, and the reason <c>all</c> carries a caution in the user
/// documentation: a file whose parse errors are suppressed still fails to parse. Only the report
/// goes away, so the features that need a good tree stay degraded with nothing on screen saying why.
///
/// <c>#pragma warning disable</c> is still SCANNED, undocumented, as an alias for the same thing.
/// The word only ever cost a regex group, and it was the published spelling for the two weeks
/// between the pragma landing and being renamed — long enough for files to exist, short enough that
/// nothing outside this tree can carry one, which is why it is accepted rather than taught.
///
/// <c>all</c> is accepted in place of a code, and codes may be written bare (<c>5014</c>) or
/// prefixed the way the editor displays them (<c>gscode-5014</c>), because that is what is on
/// screen when someone decides to suppress one.
///
/// 1.5's <c>// gscode ignore</c> is scanned here too, as an alias rather than a second mechanism:
/// it becomes a <see cref="PragmaTarget.AllCodes"/> disable scoped to the one line below the
/// comment. Files carrying it are already written, and a suppression that silently stops
/// suppressing is the worst kind of regression — the diagnostics come back, and nothing on screen
/// says why.
/// </summary>
public static class PragmaDirectives
{
    // `warning` is optional, and that one group is the whole of the compatibility with the earlier
    // spelling. It is non-capturing because nothing downstream may branch on which form was
    // written: the two mean exactly the same thing, and a difference the parser records is a
    // difference somebody eventually acts on.
    private static readonly Regex s_pattern = new(
        @"#pragma\s+(?:warning\s+)?(?<action>disable|restore)\s+(?<target>[A-Za-z0-9\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every directive in the file, including the ignore alias, in source order.</summary>
    public static ImmutableArray<PragmaDirective> Scan(ImmutableArray<Token> tokens, Core.Text.SourceText text)
    {
        ImmutableArray<PragmaDirective>.Builder directives = ImmutableArray.CreateBuilder<PragmaDirective>();

        foreach ( Token token in tokens )
        {
            if ( token.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment) )
            {
                continue;
            }

            ReadOnlySpan<char> comment = token.GetText(text);

            if ( IsIgnoreComment(comment) )
            {
                // 1.5 keyed off the comment's END line, so a block comment covers the line below
                // where it closes rather than below where it opened. A line comment's range ends
                // on the line it started on, so one expression serves both.
                directives.Add(new PragmaDirective(
                    token.Range.End.Line + 1,
                    Disable: true,
                    PragmaTarget.AllCodes,
                    Code: 0,
                    PragmaScope.OneLine));
                continue;
            }

            // The word has to be present before the string and the regex are worth paying for.
            // This runs over every comment of every file on every analysis, and a doc-commented
            // script has hundreds; almost none of them contain a pragma. Matching the regex's
            // IgnoreCase here, so the cheap test never disagrees with the expensive one.
            if ( !comment.Contains("#pragma", StringComparison.OrdinalIgnoreCase) )
            {
                continue;
            }

            foreach ( Match match in s_pattern.Matches(comment.ToString()) )
            {
                if ( !TryParseTarget(match.Groups["target"].Value, out PragmaTarget target, out int code) )
                {
                    continue;
                }

                directives.Add(new PragmaDirective(
                    token.Range.Start.Line,
                    string.Equals(match.Groups["action"].Value, "disable", StringComparison.OrdinalIgnoreCase),
                    target,
                    code));
            }
        }

        return directives.ToImmutable();
    }

    /// <summary>
    /// Whether a diagnostic at <paramref name="line"/> is suppressed.
    ///
    /// Read in source order rather than as nested scopes: a later directive simply replaces the
    /// state an earlier one set. Scoping would have to decide what an unmatched <c>disable</c>
    /// means at the end of a file, and "it stays off" is both the obvious answer and the one this
    /// gives for free.
    ///
    /// A <see cref="PragmaScope.OneLine"/> directive stands outside that running state in both
    /// directions: a <c>restore</c> above it cannot undo it, and it cannot leak onto the lines
    /// below. That is what makes the <c>// gscode ignore</c> alias an annotation on one line
    /// rather than a third way of opening a region.
    /// </summary>
    public static bool IsSuppressed(ImmutableArray<PragmaDirective> directives, GscDiagnosticCode code, int line)
    {
        bool suppressed = false;

        foreach ( PragmaDirective directive in directives )
        {
            if ( directive.Target == PragmaTarget.Format )
            {
                continue;
            }

            bool matchesCode = directive.Target == PragmaTarget.AllCodes || directive.Code == (int)code;

            if ( directive.Scope == PragmaScope.OneLine )
            {
                if ( directive.Line == line && matchesCode && directive.Disable )
                {
                    return true;
                }

                continue;
            }

            if ( directive.Line > line )
            {
                continue;
            }

            if ( matchesCode )
            {
                suppressed = directive.Disable;
            }
        }

        return suppressed;
    }

    /// <summary>Whether the formatter is switched off at <paramref name="line"/>.</summary>
    public static bool IsFormatDisabled(ImmutableArray<PragmaDirective> directives, int line)
    {
        bool disabled = false;

        foreach ( PragmaDirective directive in directives )
        {
            if ( directive.Line > line || directive.Target != PragmaTarget.Format )
            {
                continue;
            }

            disabled = directive.Disable;
        }

        return disabled;
    }

    /// <summary>
    /// 1.5's <c>// gscode ignore</c> / <c>/* gsc ignore */</c>, matching that release's
    /// <c>^(?://|/\*)\s*(?:gscode|gsc)\s+ignore\b</c> exactly — including its case sensitivity,
    /// since every file carrying one was written against it.
    /// </summary>
    private static bool IsIgnoreComment(ReadOnlySpan<char> comment)
    {
        // Hand-matched rather than run through a Regex: this sits on the same
        // every-comment-of-every-file path the #pragma pre-check exists to protect, and the shape
        // is anchored at the opener, so two span comparisons settle it.
        if ( comment.Length < 2 || comment[0] != '/' || (comment[1] != '/' && comment[1] != '*') )
        {
            return false;
        }

        ReadOnlySpan<char> rest = comment[2..].TrimStart();

        if ( rest.StartsWith("gscode", StringComparison.Ordinal) )
        {
            rest = rest["gscode".Length..];
        }
        else if ( rest.StartsWith("gsc", StringComparison.Ordinal) )
        {
            rest = rest["gsc".Length..];
        }
        else
        {
            return false;
        }

        // \s+ : "gscodeignore" and "gscodex ignore" are not this.
        if ( rest.Length == 0 || !char.IsWhiteSpace(rest[0]) )
        {
            return false;
        }

        rest = rest.TrimStart();
        if ( !rest.StartsWith("ignore", StringComparison.Ordinal) )
        {
            return false;
        }

        // \b : "gscode ignores the return value" is prose, and prose must not switch a file's
        // diagnostics off.
        rest = rest["ignore".Length..];
        return rest.Length == 0 || !(char.IsLetterOrDigit(rest[0]) || rest[0] == '_');
    }

    private static bool TryParseTarget(string written, out PragmaTarget target, out int code)
    {
        target = PragmaTarget.Code;
        code = 0;

        if ( string.Equals(written, "format", StringComparison.OrdinalIgnoreCase) )
        {
            target = PragmaTarget.Format;
            return true;
        }

        if ( string.Equals(written, "all", StringComparison.OrdinalIgnoreCase) )
        {
            target = PragmaTarget.AllCodes;
            return true;
        }

        // "gscode-5014" is what the editor puts on screen, so it is what gets copied.
        string digits = written.StartsWith("gscode-", StringComparison.OrdinalIgnoreCase)
            ? written["gscode-".Length..]
            : written;

        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out code);
    }
}
