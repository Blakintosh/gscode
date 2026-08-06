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

/// <summary>One <c>disable</c> or <c>restore</c>, at the line it was written on.</summary>
public readonly record struct PragmaDirective(int Line, bool Disable, PragmaTarget Target, int Code);

/// <summary>
/// In-source pragmas, carried INSIDE COMMENTS.
///
///     // #pragma warning disable 5014
///     doSomethingTheLintDoesNotUnderstand();
///     // #pragma warning restore 5014
///
///     // #pragma warning disable format
///     matrix = [ 1, 0, 0,
///                0, 1, 0 ];
///     // #pragma warning restore format
///
/// A comment is not a choice of style here but the only place they can live: GSC's linker reads
/// the file itself, so anything outside a comment would have to be real syntax the language does
/// not have, and every script carrying one would stop compiling.
///
/// The spelling deliberately matches C#'s <c>#pragma warning disable</c> rather than inventing
/// one. Anyone reaching for this already knows what it means, and the shape carries its own
/// documentation — <c>disable</c> and <c>restore</c> say which way they go, which
/// <c>on</c>/<c>off</c> does not once two of them are nested.
///
/// <c>all</c> is accepted in place of a code, and codes may be written bare (<c>5014</c>) or
/// prefixed the way the editor displays them (<c>gscode-5014</c>), because that is what is on
/// screen when someone decides to suppress one.
/// </summary>
public static class PragmaDirectives
{
    private static readonly Regex s_pattern = new(
        @"#pragma\s+warning\s+(?<action>disable|restore)\s+(?<target>[A-Za-z0-9\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Every pragma in the file, in source order.</summary>
    public static ImmutableArray<PragmaDirective> Scan(ImmutableArray<Token> tokens, Core.Text.SourceText text)
    {
        ImmutableArray<PragmaDirective>.Builder directives = ImmutableArray.CreateBuilder<PragmaDirective>();

        foreach ( Token token in tokens )
        {
            if ( token.Kind is not (TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment) )
            {
                continue;
            }

            // The word has to be present before the string and the regex are worth paying for.
            // This runs over every comment of every file on every analysis, and a doc-commented
            // script has hundreds; almost none of them contain a pragma. Matching the regex's
            // IgnoreCase here, so the cheap test never disagrees with the expensive one.
            ReadOnlySpan<char> comment = token.GetText(text);
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
    /// </summary>
    public static bool IsSuppressed(ImmutableArray<PragmaDirective> directives, GscDiagnosticCode code, int line)
    {
        bool suppressed = false;

        foreach ( PragmaDirective directive in directives )
        {
            if ( directive.Line > line || directive.Target == PragmaTarget.Format )
            {
                continue;
            }

            if ( directive.Target == PragmaTarget.AllCodes || directive.Code == (int)code )
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
