using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
using GSCode.Parser.Extraction;
using GSCode.Parser.Lexing;

namespace GSCode.Server.Formatting;

/// <summary>
/// A whitespace-only GSC/CSC formatter. It emits every non-trivia token verbatim and only
/// recomputes the whitespace around them: Allman braces, one statement per line, indentation
/// from brace depth, and blank-line runs capped. Comments, dev blocks, macros, and disabled
/// branches pass through untouched. See <see cref="FormatOptions"/> for what is configurable.
///
/// Brace style is deliberately NOT configurable. Allman is not a preference here, it is the
/// language's convention: the stock scripts open 51,048 braces on their own line and 37 at the
/// end of a statement.
///
/// Two safety properties make it impossible to corrupt code: it refuses to format a file
/// with lex/parse errors, and it re-lexes its own output and returns the original unchanged
/// if the non-trivia token stream is not byte-for-byte identical to the input's.
/// </summary>
public static partial class GscFormatter
{
    /// <summary>A single text edit: the source range to replace and its replacement text.</summary>
    public readonly record struct FormatEdit(TextRange Range, string NewText);

    /// <summary>
    /// The formatting result as LOCAL edits — one inside each line the formatter changed in place,
    /// one for each run of lines it split, joined or moved, and none for an unchanged line. All three
    /// formatting requests (whole, range, on-type) share this.
    ///
    /// Diffing by LINES rather than returning one edit spanning the first change to the last: a
    /// document-wide reindent is nearly the whole file, and an editor preserves the caret by mapping
    /// its offset through the edits — so a caret sitting inside one big replacement has nowhere to
    /// map to and snaps to the edit's end. Keeping every unchanged line out of the edit set instead
    /// keeps a caret resting on one untouched.
    ///
    /// The edits together reproduce <see cref="Format"/>'s output exactly, are ordered, and never
    /// overlap or touch — <see cref="AppendEdit"/> joins any two that would — so they satisfy the
    /// LSP's requirements for a multi-edit response.
    /// </summary>
    /// <param name="casing">
    /// The spellings names take under <see cref="FormatOptions.FixCasing"/>. The formatter cannot see
    /// the workspace, so the caller answers; without it only keywords are fixed.
    /// </param>
    public static ImmutableArray<FormatEdit> FormatMinimalEdits(
        ParseResult result, FormatOptions? options = null, ICasingLookup? casing = null)
    {
        string? formatted = Format(result, options, casing);
        if ( formatted is null )
        {
            return [];
        }

        string original = result.Text.Text;
        if ( string.Equals(original, formatted, StringComparison.Ordinal) )
        {
            return [];
        }

        ImmutableArray<PragmaDirective> directives = PragmaDirectives.Scan(result.Lexed.Tokens, result.Text);
        formatted = RestoreProtectedLines(directives, original, formatted, out bool lineCountMatched);

        ImmutableArray<FormatEdit> edits = DiffByLines(result.Text, original, formatted);

        // The line-for-line restore is exact when the formatter kept the line count, which is the
        // ordinary case for a whitespace-only pass. When it did not — blank-line capping removes
        // lines — the lines no longer correspond and the coarser filter takes over.
        return lineCountMatched ? edits : DropEditsInProtectedRegions(directives, edits);
    }

    /// <summary>
    /// Puts the ORIGINAL text back on every line inside a protected region, so the diff that
    /// follows sees them as unchanged and emits nothing for them.
    ///
    /// Done before diffing rather than by filtering edits afterwards, because a hunk is a run of
    /// CONSECUTIVE changed lines: the pragma comments themselves get reindented, which joins them
    /// to the protected lines and to whatever follows, and dropping that hunk would take the
    /// unprotected code with it. Making the protected lines identical splits the hunk exactly where
    /// the region ends.
    /// </summary>
    private static string RestoreProtectedLines(
        ImmutableArray<PragmaDirective> directives, string original, string formatted, out bool lineCountMatched)
    {
        lineCountMatched = false;

        if ( directives.IsEmpty )
        {
            return formatted;
        }

        List<LineSpan> originalLines = SplitLines(original);
        List<LineSpan> formattedLines = SplitLines(formatted);
        if ( originalLines.Count != formattedLines.Count )
        {
            return formatted;
        }

        lineCountMatched = true;

        // LineSpan.Text carries its own line terminator, so the lines are simply concatenated —
        // adding a separator here would double every newline.
        StringBuilder rebuilt = new(formatted.Length);
        for ( int line = 0; line < formattedLines.Count; line++ )
        {
            bool isProtected = PragmaDirectives.IsFormatDisabled(directives, line);
            rebuilt.Append(isProtected ? originalLines[line].Text : formattedLines[line].Text);
        }

        return rebuilt.ToString();
    }

    /// <summary>
    /// Drops any edit touching a region the author switched the formatter off for.
    ///
    /// Filtering EDITS rather than reformatting differently is what makes this safe. The formatter
    /// still runs over the whole file and still passes its corruption guard on the whole file; only
    /// the resulting changes to protected lines are discarded, so a bug here can leave code
    /// unformatted but can never rewrite it wrongly.
    ///
    /// An edit spanning the boundary is dropped whole. Splitting it would mean re-deriving what the
    /// formatter intended for each half, and refusing to touch a hand-laid-out block is exactly
    /// what was asked for.
    /// </summary>
    private static ImmutableArray<FormatEdit> DropEditsInProtectedRegions(
        ImmutableArray<PragmaDirective> directives, ImmutableArray<FormatEdit> edits)
    {
        if ( directives.IsEmpty || edits.IsEmpty )
        {
            return edits;
        }

        ImmutableArray<FormatEdit>.Builder kept = ImmutableArray.CreateBuilder<FormatEdit>();
        foreach ( FormatEdit edit in edits )
        {
            bool protectedRegion = false;
            for ( int line = edit.Range.Start.Line; line <= edit.Range.End.Line && !protectedRegion; line++ )
            {
                protectedRegion = PragmaDirectives.IsFormatDisabled(directives, line);
            }

            if ( !protectedRegion )
            {
                kept.Add(edit);
            }
        }

        return kept.ToImmutable();
    }

    private static ImmutableArray<FormatEdit> DiffByLines(SourceText text, string original, string formatted)
    {
        List<LineSpan> originalLines = SplitLines(original);
        List<string> formattedLines = [.. SplitLines(formatted).Select(static span => span.Text)];

        List<string> originalKeys = [.. originalLines.Select(static span => LineDiff.KeyOf(span.Text))];
        List<string> formattedKeys = [.. formattedLines.Select(LineDiff.KeyOf)];

        ImmutableArray<FormatEdit>.Builder edits = ImmutableArray.CreateBuilder<FormatEdit>();

        List<LinePair> pairs = LineDiff.Match(originalKeys, formattedKeys);

        int originalPosition = 0;
        int formattedPosition = 0;
        foreach ( LinePair pair in pairs )
        {
            // Lines between two matches were added, removed or split by the formatter: one edit.
            if ( pair.Original > originalPosition || pair.Formatted > formattedPosition )
            {
                AddUnpairedRun(
                    edits, text, originalLines, formattedLines,
                    originalPosition, pair.Original, formattedPosition, pair.Formatted);
            }

            // A matched line whose whitespace or casing changed is an edit of its own, so changes
            // stay where they are rather than joining into one region across unchanged lines.
            if ( !string.Equals(originalLines[pair.Original].Text, formattedLines[pair.Formatted], StringComparison.Ordinal) )
            {
                AddLineEdit(edits, text, originalLines[pair.Original], formattedLines[pair.Formatted]);
            }

            originalPosition = pair.Original + 1;
            formattedPosition = pair.Formatted + 1;
        }

        if ( originalLines.Count > originalPosition || formattedLines.Count > formattedPosition )
        {
            AddUnpairedRun(
                edits, text, originalLines, formattedLines,
                originalPosition, originalLines.Count, formattedPosition, formattedLines.Count);
        }

        return edits.ToImmutable();
    }

    /// <summary>
    /// Emits the edit for a run of lines left unpaired, less any lines at either end that read the
    /// same on both sides. Blank lines all share one key, so the pairing can leave a blank line
    /// unpaired on both sides at once; replacing it with itself would swallow a line the caret may
    /// rest on.
    /// </summary>
    private static void AddUnpairedRun(
        ImmutableArray<FormatEdit>.Builder edits,
        SourceText text,
        List<LineSpan> originalLines,
        List<string> formattedLines,
        int originalStart,
        int originalEnd,
        int formattedStart,
        int formattedEnd)
    {
        while ( originalStart < originalEnd && formattedStart < formattedEnd
            && string.Equals(originalLines[originalStart].Text, formattedLines[formattedStart], StringComparison.Ordinal) )
        {
            originalStart++;
            formattedStart++;
        }

        while ( originalStart < originalEnd && formattedStart < formattedEnd
            && string.Equals(originalLines[originalEnd - 1].Text, formattedLines[formattedEnd - 1], StringComparison.Ordinal) )
        {
            originalEnd--;
            formattedEnd--;
        }

        if ( originalStart < originalEnd || formattedStart < formattedEnd )
        {
            AddEdit(edits, text, originalLines, formattedLines, originalStart, originalEnd, formattedStart, formattedEnd);
        }
    }

    /// <summary>
    /// Emits the edit for one line the formatter changed in place: only the characters between the
    /// part the two versions share at the start and the part they share at the end, and never the
    /// line ending.
    /// </summary>
    /// <remarks>
    /// Before applying a formatter's edits VS Code merges every pair that TOUCHES, end to start, and
    /// gives up diffing a merged edit past 100,000 characters. Whole-line edits touch whenever two
    /// changed lines are adjacent, so a file reindented throughout became one edit again on the
    /// editor's side, applied whole, with the caret thrown to its end. Kept inside the line, an edit
    /// is separated from the next line's by at least the line break, and nothing merges.
    /// </remarks>
    private static void AddLineEdit(
        ImmutableArray<FormatEdit>.Builder edits, SourceText text, LineSpan originalLine, string formattedLine)
    {
        string originalContent = WithoutLineEnding(originalLine.Text);
        string formattedContent = WithoutLineEnding(formattedLine);

        // A changed line ending is a change to the whole line; replace it outright.
        bool sameEnding = originalLine.Text.Length - originalContent.Length == formattedLine.Length - formattedContent.Length
            && originalLine.Text.EndsWith(formattedLine[formattedContent.Length..], StringComparison.Ordinal);
        if ( !sameEnding )
        {
            TextRange whole = new(
                text.GetPosition(originalLine.Offset), text.GetPosition(originalLine.Offset + originalLine.Text.Length));
            AppendEdit(edits, new FormatEdit(whole, formattedLine));
            return;
        }

        int shorter = Math.Min(originalContent.Length, formattedContent.Length);
        int prefix = 0;
        while ( prefix < shorter && originalContent[prefix] == formattedContent[prefix] )
        {
            prefix++;
        }

        int suffix = 0;
        while ( suffix < shorter - prefix
            && originalContent[originalContent.Length - 1 - suffix] == formattedContent[formattedContent.Length - 1 - suffix] )
        {
            suffix++;
        }

        int start = originalLine.Offset + prefix;
        int end = originalLine.Offset + originalContent.Length - suffix;
        string replacement = formattedContent.Substring(prefix, formattedContent.Length - suffix - prefix);
        AppendEdit(edits, new FormatEdit(new TextRange(text.GetPosition(start), text.GetPosition(end)), replacement));
    }

    private static string WithoutLineEnding(string line)
    {
        if ( line.EndsWith("\r\n", StringComparison.Ordinal) )
        {
            return line[..^2];
        }

        return line.EndsWith('\n') ? line[..^1] : line;
    }

    /// <summary>
    /// Emits one edit replacing original lines <c>[originalStart, originalEnd)</c> with formatted
    /// lines <c>[formattedStart, formattedEnd)</c>. Each line carries its own newline, so the line
    /// boundaries are exactly the offsets to cut at and a line range maps to a contiguous span.
    /// </summary>
    private static void AddEdit(
        ImmutableArray<FormatEdit>.Builder edits,
        SourceText text,
        List<LineSpan> originalLines,
        List<string> formattedLines,
        int originalStart,
        int originalEnd,
        int formattedStart,
        int formattedEnd)
    {
        int startOffset = originalStart < originalLines.Count ? originalLines[originalStart].Offset : text.Length;
        int endOffset = originalEnd < originalLines.Count ? originalLines[originalEnd].Offset : text.Length;

        StringBuilder replacement = new();
        for ( int index = formattedStart; index < formattedEnd; index++ )
        {
            replacement.Append(formattedLines[index]);
        }

        TextRange range = new(text.GetPosition(startOffset), text.GetPosition(endOffset));
        AppendEdit(edits, new FormatEdit(range, replacement.ToString()));
    }

    /// <summary>
    /// Adds an edit after the ones before it, joining it to the previous edit when the two touch —
    /// so no two edits handed to the editor ever share an end and a start.
    /// </summary>
    /// <remarks>
    /// VS Code joins a formatter's edits that touch before applying them, and whole-line edits on
    /// adjacent lines joined back into one replacement spanning thousands of lines. The one shape
    /// still able to touch is a run of added or split lines followed by an edit at column 0 of the
    /// next line; joining it here costs a few lines, and leaves nothing for the editor to join.
    /// The caret's own position across a large format is restored by the client — see
    /// client/src/caretRestore.ts for why VS Code cannot keep it there by itself.
    /// </remarks>
    private static void AppendEdit(ImmutableArray<FormatEdit>.Builder edits, FormatEdit edit)
    {
        if ( edits.Count > 0 && edits[^1].Range.End == edit.Range.Start )
        {
            FormatEdit previous = edits[^1];
            edits[^1] = new FormatEdit(new TextRange(previous.Range.Start, edit.Range.End), previous.NewText + edit.NewText);
            return;
        }

        edits.Add(edit);
    }

    /// <summary>A source line and its start offset. The text keeps its trailing newline, if any.</summary>
    private readonly record struct LineSpan(int Offset, string Text);

    private static List<LineSpan> SplitLines(string source)
    {
        List<LineSpan> lines = [];
        int start = 0;
        for ( int index = 0; index < source.Length; index++ )
        {
            if ( source[index] == '\n' )
            {
                lines.Add(new LineSpan(start, source.Substring(start, index - start + 1)));
                start = index + 1;
            }
        }

        // A final line without a trailing newline. When the text ends on '\n', start == length and
        // there is nothing left, which is correct — no empty line is invented.
        if ( start < source.Length )
        {
            lines.Add(new LineSpan(start, source.Substring(start)));
        }

        return lines;
    }

    /// <summary>
    /// Produces the formatted document text, or null when formatting is refused (syntax
    /// errors) or would not be safe (the token stream would change). A null result means
    /// "make no edits".
    /// </summary>
    public static string? Format(
        ParseResult result, FormatOptions? requested = null, ICasingLookup? casing = null)
    {
        // Nullable rather than a `default` struct sentinel: default(FormatOptions) is all-zero,
        // which reads as a perfectly valid "no indent, no padding" configuration and silently
        // formatted everything flat.
        FormatOptions options = requested ?? FormatOptions.Default;

        if ( HasSyntaxErrors(result) )
        {
            return null;
        }

        ImmutableArray<Token> tokens = result.Lexed.Tokens;
        List<SignificantToken> significant = CollectSignificant(tokens, result.Text);
        if ( significant.Count == 0 )
        {
            return null;
        }

        // Spacing that depends on more than the two tokens either side of the gap.
        TokenRoles roles = TokenRoles.Of(significant);

        // The spelling each token is written with: null keeps the source's. Only casing ever
        // differs, and only when FixCasing asks for it.
        string?[] spellings = options.FixCasing
            ? CasingFixes(significant, result, roles, casing)
            : new string?[significant.Count];

        string formatted = Reflow(significant, result.Text, options, roles, spellings);

        // Corruption guard: the reflow must preserve the exact non-trivia token stream, with the
        // casing fixes as the only permitted difference.
        if ( !TokenStreamMatches(significant, result.Text, formatted, spellings) )
        {
            return null;
        }

        // Directive sorting runs AFTER the gate, because it deliberately moves tokens and would
        // trip it. It carries its own equality check instead -- see DirectiveSorter.
        if ( options.SortDirectives )
        {
            formatted = DirectiveSorter.Sort(formatted) ?? formatted;
        }

        // Consecutive alignment is also a post-pass. Columns first — padding subscript and argument
        // interiors equalises the left-hand sides — then the operators, which the equalised sides
        // then line up almost for free.
        if ( options.AlignConsecutive )
        {
            string unaligned = formatted;
            formatted = ColumnAligner.Align(formatted, options.AlignMaxPadding);
            formatted = AssignmentAligner.Align(formatted, options.AlignMaxPadding);

            // The aligners only ever widen gaps, but they rewrite lines by column offset, and the
            // gate above ran before them. Checked again here so a wrong offset costs the alignment
            // rather than the file; the corpus gates test the same property, this enforces it.
            if ( !SameTokens(unaligned, formatted) )
            {
                formatted = unaligned;
            }
        }

        return InDocumentLineEndings(formatted, result.Text.Text);
    }

    /// <summary>
    /// The output in the line endings the document already uses. Every pass above writes '\n', so
    /// without this a clean CRLF file came back as one edit spanning the whole file on every
    /// request, and on-type formatting kept that edit on every keystroke.
    /// </summary>
    /// <remarks>
    /// Normalised to '\n' before converting because a multi-line block comment is emitted verbatim,
    /// its own '\r\n' included, and converting that a second time would write '\r\r\n'. The editor
    /// owns the file's line endings, so the first line break decides; a mixed file is rare enough
    /// that one answer for all of it is the right trade.
    /// </remarks>
    private static string InDocumentLineEndings(string formatted, string original)
    {
        int firstNewline = original.IndexOf('\n');
        bool usesCrlf = firstNewline > 0 && original[firstNewline - 1] == '\r';
        if ( !usesCrlf )
        {
            return formatted;
        }

        return formatted.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }

    /// <summary>One significant (non-trivia) token plus how many newlines preceded it in the source.</summary>
    private readonly record struct SignificantToken(Token Token, int NewlinesBefore);

    private static List<SignificantToken> CollectSignificant(ImmutableArray<Token> tokens, SourceText text)
    {
        List<SignificantToken> significant = [];
        int newlineRun = 0;

        foreach ( Token token in tokens )
        {
            if ( token.Kind == TokenKind.Newline )
            {
                newlineRun++;
                continue;
            }

            if ( token.Kind == TokenKind.Whitespace || token.Kind == TokenKind.EndOfFile )
            {
                continue;
            }

            significant.Add(new SignificantToken(token, newlineRun));
            newlineRun = 0;
        }

        return significant;
    }

    private static string Reflow(
        List<SignificantToken> significant, SourceText text, FormatOptions options, TokenRoles roles, string?[] spellings)
    {
        StringBuilder output = new();
        int depth = 0;
        int parenDepth = 0;

        // Brace depth puts `case` one level inside the switch, but the statements UNDER a label
        // need one more, and the label itself must not get it. Each open brace records whether it
        // belongs to a switch and whether a case label is currently open inside it.
        List<SwitchBlock> blocks = [];
        bool switchHeaderSeen = false;

        // Brace depth alone cannot indent an unbraced control-flow body — `if ( x )` with its
        // statement on the next line opens no brace, so the body would land in the `if`'s own
        // column. This tracks bodies that are owed an indent without one.
        UnbracedBodyTracker unbraced = new();

        // Whether each open parenthesis is a CALL's (`foo(`) or a control-flow/grouping one
        // (`if (`, `= (`), so its interior can take the padding rule the user chose for that kind.
        // The closer needs the same answer, hence a stack rather than a flag.
        List<bool> callParens = [];

        // Whether each open parenthesis belongs to a control-flow header, directly or nested inside
        // one. A header split across lines lines its continuation lines up under the first
        // character inside its '(' -- the shape stock writes a long chain of `&&` conditions in.
        List<bool> headerParens = [];

        // Where a split header's continuation lines start: the column, counted from the end of the
        // header line's indentation, of the first token inside the header's '('. -1 when no header
        // is open, or when that token itself starts a new line and there is nothing to align under.
        int headerAlign = -1;
        bool headerAlignPending = false;

        // Where the current line's text starts in the output, after its indentation.
        int lineContentStart = 0;

        // A closed block is followed by a blank line before the next statement: stock puts one
        // there 15,940 times against 3,012. A do-while's block is not closed until its tail's ';'.
        bool blankOwed = false;
        bool doSeen = false;
        bool doTailPending = false;
        bool inDoTail = false;

        // Open parentheses and brackets, for the continuation indent: a line that starts inside one
        // continues a statement and sits one level deeper. Separate from parenDepth because it
        // counts brackets too, and it is reset at every brace -- no brace can sit inside a
        // parenthesis in GSC, so an unbalanced pair in a disabled #if branch cannot push the
        // whole rest of the file one level right.
        int openGroups = 0;

        for ( int index = 0; index < significant.Count; index++ )
        {
            Token token = significant[index].Token;
            int newlinesBefore = significant[index].NewlinesBefore;
            bool insideCallParen = false;

            // Asked before the closer below is popped, so a header's own ')' is joined too.
            bool insideHeader = headerParens.Count > 0 && headerParens[^1];

            // A #define's body is one logical line of text for the preprocessor, not statements:
            // its braces open no block and its ';' ends nothing, so none of them move a line.
            bool inDirective = roles.InDirective[index];
            bool structuralBrace = !inDirective && (token.Kind is TokenKind.OpenBrace or TokenKind.CloseBrace);

            // Closers dedent before this line's indent is computed. A dev block only counts when
            // the setting asks for it: `/# … #/` is a runtime switch, not a scope -- the
            // engine jumps over it when dev script is off -- and stock keeps it flush 316 times
            // to 194, but that is a split rather than a rule.
            if ( (token.Kind == TokenKind.CloseBrace && structuralBrace)
                || (token.Kind == TokenKind.DevBlockClose && options.IndentDevBlocks) )
            {
                depth = Math.Max(0, depth - 1);
            }

            if ( token.Kind == TokenKind.CloseParen )
            {
                parenDepth = Math.Max(0, parenDepth - 1);

                if ( callParens.Count > 0 )
                {
                    insideCallParen = callParens[^1];
                    callParens.RemoveAt(callParens.Count - 1);
                }

                if ( headerParens.Count > 0 )
                {
                    headerParens.RemoveAt(headerParens.Count - 1);
                }

                if ( headerParens.Count == 0 || !headerParens[^1] )
                {
                    headerAlign = -1;
                }
            }

            bool closesDoBody = false;
            if ( token.Kind == TokenKind.CloseBrace && structuralBrace && blocks.Count > 0 )
            {
                closesDoBody = blocks[^1].IsDo;
                blocks.RemoveAt(blocks.Count - 1);
            }

            // The token after a do body's '}' is its `while` tail, which belongs to the block.
            if ( doTailPending )
            {
                doTailPending = false;
                if ( token.Kind == TokenKind.While )
                {
                    inDoTail = true;
                }
                else
                {
                    blankOwed = true;
                }
            }

            if ( token.Kind is TokenKind.CloseParen or TokenKind.CloseBracket )
            {
                openGroups = Math.Max(0, openGroups - 1);
            }

            if ( structuralBrace )
            {
                openGroups = 0;
                headerParens.Clear();
                headerAlign = -1;
                headerAlignPending = false;
            }

            // A label sits at the block's own level, so it does not get its own case indent.
            bool isLabel = token.Kind is TokenKind.Case or TokenKind.Default;
            int caseIndents = OpenCaseIndents(blocks, excludeInnermost: isLabel);

            // Flush labels take back the level each open switch's brace gave them, which moves
            // the statements under a label back with them.
            if ( !options.IndentCaseLabels )
            {
                caseIndents -= OpenSwitches(blocks);
            }

            if ( !inDirective )
            {
                unbraced.BeforeToken(token.Kind);
            }

            if ( index == 0 )
            {
                AppendToken(output, token, text, spellings[index]);
            }
            else
            {
                Token previous = significant[index - 1].Token;
                bool trailingComment = LineFacts.IsComment(token.Kind) && newlinesBefore == 0;

                if ( previous.Kind == TokenKind.OpenParen && callParens.Count > 0 )
                {
                    insideCallParen = callParens[^1];
                }

                if ( ShouldBreak(previous.Kind, token.Kind, newlinesBefore, trailingComment, parenDepth, roles.LabelColon[index - 1], inDirective) )
                {
                    int blankLines = Math.Clamp(newlinesBefore - 1, 0, options.MaxBlankLines);
                    if ( blankOwed && !HugsTheBlockAbove(token.Kind) )
                    {
                        blankLines = Math.Max(blankLines, Math.Min(1, options.MaxBlankLines));
                    }
                    output.Append('\n', 1 + blankLines);
                    if ( insideHeader && headerAlign >= 0 )
                    {
                        // Tabs to the header line's own level, then spaces to align under its '( '.
                        AppendIndent(output, depth + unbraced.PendingIndents + caseIndents, options);
                        output.Append(' ', headerAlign);
                    }
                    else
                    {
                        int continuation = openGroups > 0 || roles.ContinuesLine[index] ? 1 : 0;
                        AppendIndent(output, depth + unbraced.PendingIndents + caseIndents + continuation, options);
                    }

                    lineContentStart = output.Length;

                    // A header whose '(' ends its line has nothing to align under; its continuation
                    // lines take the ordinary one-level continuation instead.
                    headerAlignPending = false;
                }
                else
                {
                    // The token before the operator decides whether `-`, `+` and `&` are unary.
                    TokenKind beforePrevious = index >= 2 ? significant[index - 2].Token.Kind : TokenKind.OpenBrace;
                    if ( roles.TightBefore[index] )
                    {
                        // Nothing between the two tokens.
                    }
                    else if ( roles.SpaceBefore[index] )
                    {
                        output.Append(' ');
                    }
                    else
                    {
                        output.Append(Separator(beforePrevious, previous.Kind, token.Kind, insideCallParen, options));
                    }
                }

                if ( headerAlignPending )
                {
                    headerAlign = output.Length - lineContentStart;
                    headerAlignPending = false;
                }

                AppendToken(output, token, text, spellings[index]);
            }

            // Openers indent everything that follows -- again, dev blocks only by setting.
            if ( token.Kind == TokenKind.OpenBrace && structuralBrace )
            {
                depth++;
                blocks.Add(new SwitchBlock { IsSwitch = switchHeaderSeen, IsDo = doSeen });
                doSeen = false;
                switchHeaderSeen = false;
            }

            if ( token.Kind == TokenKind.DevBlockOpen && options.IndentDevBlocks )
            {
                depth++;
            }

            if ( token.Kind == TokenKind.Switch )
            {
                switchHeaderSeen = true;
            }

            // Everything after the label's ':' belongs to the case body.
            if ( isLabel && blocks.Count > 0 && blocks[^1].IsSwitch )
            {
                blocks[^1].CaseOpen = true;
            }

            // A label whose body is a braced block takes no case indent: the braces supply the
            // level, so `{`, `}` and the `break;` after them sit in the label's column. Stock
            // writes it that way 59 times against 47, and every time with the break beside them.
            bool bracedCaseBody = roles.LabelColon[index] && NextIsOpenBrace(significant, index);
            if ( bracedCaseBody && blocks.Count > 0 && blocks[^1].IsSwitch )
            {
                blocks[^1].CaseOpen = false;
            }

            if ( token.Kind is TokenKind.OpenParen or TokenKind.OpenBracket )
            {
                openGroups++;
            }

            if ( token.Kind == TokenKind.OpenParen )
            {
                parenDepth++;
                callParens.Add(index > 0 && !IsGroupingParen(significant[index - 1].Token.Kind));

                bool opensHeader = index > 0 && IsControlFlowKeyword(significant[index - 1].Token.Kind);
                bool withinHeader = headerParens.Count > 0 && headerParens[^1];
                headerParens.Add(opensHeader || withinHeader);

                // The outermost header's first interior token sets the alignment column.
                if ( opensHeader && !withinHeader )
                {
                    headerAlign = -1;
                    headerAlignPending = true;
                }
            }

            if ( !inDirective )
            {
                unbraced.AfterToken(token.Kind);
            }

            // A trailing comment on the '}' line leaves the blank owed to the line after it.
            bool trailing = index > 0 && LineFacts.IsComment(token.Kind) && newlinesBefore == 0;
            if ( !trailing )
            {
                blankOwed = false;
            }

            if ( token.Kind == TokenKind.Do )
            {
                doSeen = true;
            }
            else if ( token.Kind == TokenKind.Semicolon && parenDepth == 0 )
            {
                doSeen = false;
                if ( inDoTail )
                {
                    inDoTail = false;
                    blankOwed = true;
                }
            }

            if ( token.Kind == TokenKind.CloseBrace && structuralBrace )
            {
                if ( closesDoBody )
                {
                    doTailPending = true;
                }
                else
                {
                    blankOwed = true;
                }
            }

            // The ';' ending a chain of unbraced bodies closes it the way a '}' closes a block:
            // stock puts a blank line after one 3,145 times against 526. Nested headers share that
            // ';', so the chain gets one blank line, not one per header.
            if ( !inDirective && unbraced.BodyEnded )
            {
                if ( unbraced.EndedDoBody )
                {
                    doTailPending = true;
                }
                else
                {
                    blankOwed = true;
                }
            }
        }

        output.Append('\n');
        return output.ToString();
    }

    /// <summary>Writes a token with its casing fix, when it has one.</summary>
    private static void AppendToken(StringBuilder output, Token token, SourceText text, string? spelling)
    {
        if ( spelling is not null )
        {
            output.Append(spelling);
            return;
        }

        output.Append(token.GetText(text));
    }

    /// <summary>
    /// Per-token spacing decided by context the pairwise <see cref="Separator"/> cannot see. Worked
    /// out in one pass before the reflow, so the reflow asks by index.
    /// </summary>
    private sealed class TokenRoles
    {
        /// <summary>The gap before this token is always empty.</summary>
        public required bool[] TightBefore { get; init; }

        /// <summary>The gap before this token is always one space.</summary>
        public required bool[] SpaceBefore { get; init; }

        /// <summary>This token is the ':' ending a <c>case</c> or <c>default</c> label.</summary>
        public required bool[] LabelColon { get; init; }

        /// <summary>This token starts the line after a '\' continuation, so it is indented one level.</summary>
        public required bool[] ContinuesLine { get; init; }

        /// <summary>This token is part of a <c>#define</c>'s logical line, after the directive itself.</summary>
        public required bool[] InDirective { get; init; }

        public static TokenRoles Of(List<SignificantToken> significant)
        {
            TokenRoles roles = new()
            {
                TightBefore = new bool[significant.Count],
                SpaceBefore = new bool[significant.Count],
                LabelColon = new bool[significant.Count],
                ContinuesLine = new bool[significant.Count],
                InDirective = new bool[significant.Count],
            };

            MarkFunctionPointers(significant, roles);

            // A ':' ends a label, closes a ternary, or names a base class. A label's own
            // expression can hold a ternary, so its '?'s are counted apart from the statement's.
            bool labelOpen = false;
            int labelTernaries = 0;

            for ( int index = 0; index < significant.Count; index++ )
            {
                TokenKind kind = significant[index].Token.Kind;
                switch ( kind )
                {
                    case TokenKind.DefineDirective:
                        MarkDefine(significant, index, roles);
                        MarkDirectiveLine(significant, index, roles);
                        break;
                    case TokenKind.Backslash:
                        // A '\' that ends its line continues a directive, not a path: set it off
                        // from the code before it, and indent the line it continues onto.
                        if ( index + 1 < significant.Count && significant[index + 1].NewlinesBefore > 0 )
                        {
                            roles.SpaceBefore[index] = true;
                            roles.ContinuesLine[index + 1] = true;
                        }

                        break;
                    case TokenKind.Case:
                    case TokenKind.Default:
                        labelOpen = true;
                        labelTernaries = 0;
                        break;
                    case TokenKind.QuestionMark:
                        if ( labelOpen )
                        {
                            labelTernaries++;
                        }

                        break;
                    case TokenKind.Colon:
                        if ( labelOpen && labelTernaries == 0 )
                        {
                            // `case 0:` -- tight, and the label ends its line.
                            roles.TightBefore[index] = true;
                            roles.LabelColon[index] = true;
                            labelOpen = false;
                        }
                        else
                        {
                            // `a ? b : c` and `class Derived : Base` -- spaced both sides.
                            roles.SpaceBefore[index] = true;
                            if ( labelOpen )
                            {
                                labelTernaries--;
                            }
                        }

                        break;
                    case TokenKind.Semicolon:
                    case TokenKind.OpenBrace:
                    case TokenKind.CloseBrace:
                        labelOpen = false;
                        break;
                }
            }

            return roles;
        }

        /// <summary>
        /// The tokens of a <c>#define</c>'s logical line: everything up to the first line break
        /// that is not escaped by a '\' at the end of the line before it.
        /// </summary>
        private static void MarkDirectiveLine(List<SignificantToken> significant, int directive, TokenRoles roles)
        {
            for ( int index = directive + 1; index < significant.Count; index++ )
            {
                bool escaped = significant[index - 1].Token.Kind == TokenKind.Backslash;
                if ( significant[index].NewlinesBefore > 0 && !escaped )
                {
                    return;
                }

                roles.InDirective[index] = true;
            }
        }

        /// <summary>
        /// A function pointer's <c>[[</c> and <c>]]</c> read as one token each, so the two brackets
        /// stay together while the interior takes the ordinary bracket padding:
        /// <c>self [[ level.callback ]]()</c>. The <c>[[</c> is set off from a caller before it —
        /// stock writes <c>self [[</c> 735 times against 2 — where a lone <c>[</c> after an operand
        /// is a subscript and hugs it. Only a matched pair is a pointer: the closers are found by
        /// bracket matching, so nested subscripts' <c>] ]</c> stay padded.
        /// </summary>
        private static void MarkFunctionPointers(List<SignificantToken> significant, TokenRoles roles)
        {
            List<int> open = [];
            HashSet<int> pointerInners = [];
            for ( int index = 0; index < significant.Count; index++ )
            {
                TokenKind kind = significant[index].Token.Kind;
                if ( kind == TokenKind.OpenBracket )
                {
                    bool opensPointer = index + 1 < significant.Count
                        && significant[index + 1].Token.Kind == TokenKind.OpenBracket
                        && !pointerInners.Contains(index);
                    if ( opensPointer )
                    {
                        pointerInners.Add(index + 1);
                        roles.TightBefore[index + 1] = true;
                        if ( index > 0 && EndsAnOperand(significant[index - 1].Token.Kind) )
                        {
                            roles.SpaceBefore[index] = true;
                        }
                    }

                    open.Add(index);
                }
                else if ( kind == TokenKind.CloseBracket && open.Count > 0 )
                {
                    int opener = open[^1];
                    open.RemoveAt(open.Count - 1);

                    // The inner of a pointer pair closing, with the outer closing right after it.
                    bool closesPointer = pointerInners.Contains(opener)
                        && index + 1 < significant.Count
                        && significant[index + 1].Token.Kind == TokenKind.CloseBracket;
                    if ( closesPointer )
                    {
                        roles.TightBefore[index + 1] = true;
                    }
                }
                else if ( kind is TokenKind.OpenBrace or TokenKind.CloseBrace )
                {
                    // No bracket spans a brace, so a stray one in a disabled branch stops here.
                    open.Clear();
                }
            }
        }

        /// <summary>
        /// A <c>#define</c>'s name and what follows it. Whether a macro takes parameters is decided
        /// by WHITESPACE, which the token gate cannot see: <c>#define HALF( x )</c> is function-like
        /// only because the paren touches the name, and <c>#define HALF ( 1 / 2 )</c> is an
        /// object-like macro whose body happens to start with one. Hugging the second the way a call
        /// is hugged turned it into the first, and every bare <c>HALF</c> stopped expanding. So the
        /// source's adjacency is kept exactly, and the body is always set one space off.
        /// </summary>
        private static void MarkDefine(List<SignificantToken> significant, int directive, TokenRoles roles)
        {
            int name = directive + 1;
            int next = directive + 2;
            if ( next >= significant.Count || significant[name].NewlinesBefore > 0 || significant[next].NewlinesBefore > 0 )
            {
                return;
            }

            Token nameToken = significant[name].Token;
            Token nextToken = significant[next].Token;
            bool functionLike = nextToken.Kind == TokenKind.OpenParen && nameToken.Range.End == nextToken.Range.Start;
            if ( !functionLike )
            {
                roles.SpaceBefore[next] = true;
                return;
            }

            roles.TightBefore[next] = true;

            // The body starts after the parameter list's ')', which never spans a line.
            int depth = 0;
            for ( int index = next; index < significant.Count; index++ )
            {
                if ( index > next && significant[index].NewlinesBefore > 0 )
                {
                    return;
                }

                TokenKind kind = significant[index].Token.Kind;
                if ( kind == TokenKind.OpenParen )
                {
                    depth++;
                }
                else if ( kind == TokenKind.CloseParen )
                {
                    depth--;
                    if ( depth == 0 )
                    {
                        int body = index + 1;
                        if ( body < significant.Count && significant[body].NewlinesBefore == 0 )
                        {
                            roles.SpaceBefore[body] = true;
                        }

                        return;
                    }
                }
            }
        }
    }

    /// <summary>One open brace: whether it is a switch body, and whether a case label is open in it.</summary>
    private sealed class SwitchBlock
    {
        public bool IsSwitch { get; init; }

        public bool CaseOpen { get; set; }

        /// <summary>Whether this brace is a <c>do</c> body, whose <c>while</c> tail follows the '}'.</summary>
        public bool IsDo { get; init; }
    }

    /// <summary>
    /// Whether a token sits directly under the '}' above it rather than after a blank line: a
    /// closer or a continuation of the same construct (`else`, the next label, the end of a dev
    /// block), or the `break` that ends a braced case body, which stock writes directly after
    /// its '}'.
    /// </summary>
    private static bool HugsTheBlockAbove(TokenKind kind)
    {
        return kind is TokenKind.CloseBrace
            or TokenKind.Else
            or TokenKind.Case
            or TokenKind.Default
            or TokenKind.DevBlockClose
            or TokenKind.Break;
    }

    /// <summary>
    /// How many extra levels the open case labels are worth. Nested switches each contribute one.
    /// <paramref name="excludeInnermost"/> is set when emitting a label, which belongs at its
    /// block's own level rather than inside the case body it is about to open.
    /// </summary>
    private static int OpenCaseIndents(List<SwitchBlock> blocks, bool excludeInnermost)
    {
        int total = 0;
        for ( int index = 0; index < blocks.Count; index++ )
        {
            if ( !blocks[index].CaseOpen )
            {
                continue;
            }

            bool innermostOpen = true;
            for ( int deeper = index + 1; deeper < blocks.Count; deeper++ )
            {
                if ( blocks[deeper].IsSwitch )
                {
                    innermostOpen = false;
                    break;
                }
            }

            if ( excludeInnermost && innermostOpen )
            {
                continue;
            }

            total++;
        }

        return total;
    }

    /// <summary>Whether the next token past any comments is a `{`.</summary>
    private static bool NextIsOpenBrace(List<SignificantToken> significant, int index)
    {
        for ( int next = index + 1; next < significant.Count; next++ )
        {
            TokenKind kind = significant[next].Token.Kind;
            if ( !LineFacts.IsComment(kind) )
            {
                return kind == TokenKind.OpenBrace;
            }
        }

        return false;
    }

    /// <summary>How many of the open braces are switch bodies.</summary>
    private static int OpenSwitches(List<SwitchBlock> blocks)
    {
        int total = 0;
        foreach ( SwitchBlock block in blocks )
        {
            if ( block.IsSwitch )
            {
                total++;
            }
        }

        return total;
    }

    /// <summary>
    /// Tracks control-flow bodies written without braces, which carry no brace depth of their own.
    ///
    /// A header (`if (…)`, `while (…)`, `for (…)`, `foreach (…)`) or a bare `else`/`do` is followed
    /// by either `{` — in which case brace depth already handles it — or a single statement that
    /// needs one extra level. Nested headers stack, and all of them end at the same statement, so
    /// a terminator releases every pending level at once:
    ///
    ///     if ( a )
    ///         if ( b )
    ///             doThing();   &lt;- two pending levels, both released by this `;`
    /// </summary>
    private sealed class UnbracedBodyTracker
    {
        private bool _expectingHeader;
        private int _headerParenDepth;
        private bool _awaitingBody;
        private bool _awaitingBodyFromElse;
        private bool _awaitingBodyFromDo;
        private bool _doBodyOpen;

        /// <summary>Extra indent levels owed to unbraced bodies currently open.</summary>
        public int PendingIndents { get; private set; }

        /// <summary>
        /// Whether the last token ended a chain of unbraced bodies. Nested headers all end at the
        /// same ';', so the chain ends once, however deep it was.
        /// </summary>
        public bool BodyEnded { get; private set; }

        /// <summary>Whether the chain that just ended was a <c>do</c> body, whose <c>while</c> tail follows.</summary>
        public bool EndedDoBody { get; private set; }

        /// <summary>Called before the token is written, so its own line uses the right indent.</summary>
        public void BeforeToken(TokenKind kind)
        {
            if ( !_awaitingBody )
            {
                return;
            }

            _awaitingBody = false;
            bool fromElse = _awaitingBodyFromElse;
            _awaitingBodyFromElse = false;
            bool fromDo = _awaitingBodyFromDo;
            _awaitingBodyFromDo = false;

            // A braced body needs nothing: brace depth already covers it.
            if ( kind == TokenKind.OpenBrace )
            {
                return;
            }

            // `else if` is one chained construct, not an `else` whose body is an `if`. Counting it
            // as a body left a level owed that the `if`'s own `{` never released, so a braced
            // `else if ( x ) { … }` came out one level deep with its closing brace misaligned.
            if ( fromElse && kind == TokenKind.If )
            {
                return;
            }

            PendingIndents++;
            if ( fromDo )
            {
                _doBodyOpen = true;
            }
        }

        /// <summary>Called after the token is written, to arm or release the next body.</summary>
        public void AfterToken(TokenKind kind)
        {
            BodyEnded = false;
            EndedDoBody = false;

            // A ';' inside a header's parentheses separates the clauses of a `for` rather than
            // ending a statement. As a terminator it would tear down the header mid-flight, and the
            // ')' would never arm the body of an unbraced `for ( … )`. Same root cause as the
            // line-breaking rule in ShouldBreak.
            if ( kind == TokenKind.Semicolon && _expectingHeader && _headerParenDepth > 0 )
            {
                return;
            }

            // A statement terminator ends every unbraced body stacked above it. `}` is reset
            // rather than decremented: a brace closing here means the body was braced after all,
            // or the tracker is out of step, and dropping to zero is the safe direction.
            if ( kind == TokenKind.Semicolon || kind == TokenKind.CloseBrace )
            {
                BodyEnded = kind == TokenKind.Semicolon && PendingIndents > 0;
                EndedDoBody = BodyEnded && _doBodyOpen;
                PendingIndents = 0;
                _expectingHeader = false;
                _awaitingBody = false;
                _awaitingBodyFromElse = false;
                _awaitingBodyFromDo = false;
                _doBodyOpen = false;
                return;
            }

            if ( kind is TokenKind.If or TokenKind.While or TokenKind.For or TokenKind.Foreach )
            {
                _expectingHeader = true;
                _headerParenDepth = 0;
                return;
            }

            // `else` and `do` take a body directly, with no parenthesised header between.
            if ( kind is TokenKind.Else or TokenKind.Do )
            {
                _awaitingBody = true;
                _awaitingBodyFromElse = kind == TokenKind.Else;
                _awaitingBodyFromDo = kind == TokenKind.Do;
                return;
            }

            if ( !_expectingHeader )
            {
                return;
            }

            if ( kind == TokenKind.OpenParen )
            {
                _headerParenDepth++;
            }
            else if ( kind == TokenKind.CloseParen )
            {
                _headerParenDepth--;
                if ( _headerParenDepth <= 0 )
                {
                    // The header is complete, so whatever comes next is the body.
                    _expectingHeader = false;
                    _awaitingBody = true;
                }
            }
        }
    }

    /// <summary>
    /// Decides whether the token starts a new line. Structural breaks (Allman braces, one
    /// statement per line) are forced; otherwise an original line break is preserved, which
    /// keeps newline-terminated directives (#define, #if) intact. A trailing comment stays
    /// glued to the line it annotated.
    /// </summary>
    private static bool ShouldBreak(
        TokenKind previous, TokenKind current, int newlinesBefore, bool trailingComment, int parenDepth, bool afterLabel,
        bool inDirective)
    {
        if ( trailingComment )
        {
            return false;
        }

        // Inside a #define only the author's own '\'-continued breaks exist. Forcing Allman or
        // one-statement-per-line there ended the macro at the first '{' or ';' and left the rest
        // of its body as top-level code: `#define WAIT {wait(0.05);}` became an empty macro.
        if ( inDirective )
        {
            return newlinesBefore > 0;
        }

        // A `case` or `default` label is a line of its own, and so is each of a stacked pair: stock
        // writes 2,453 labels alone on their line against 63 followed by a statement.
        if ( afterLabel )
        {
            return true;
        }

        // A line comment runs to end-of-line, so whatever follows must start a new line.
        if ( previous == TokenKind.LineComment )
        {
            return true;
        }

        if ( current == TokenKind.OpenBrace || current == TokenKind.CloseBrace || current == TokenKind.DevBlockClose )
        {
            return true;
        }

        if ( previous == TokenKind.OpenBrace || previous == TokenKind.CloseBrace || previous == TokenKind.DevBlockOpen )
        {
            return true;
        }

        // Inside parentheses a ';' separates the clauses of a `for` header rather than ending a
        // statement, so it must not break the line: `for ( i = 0; i < 10; i++ )`.
        // A ';' directly before ')' can only be an empty `for` clause -- `for ( ;; )`. The depth
        // has already been decremented for that ')' by the time this is asked, so without the
        // exclusion it reads as a statement end and the ')' is pushed onto a line of its own.
        if ( previous == TokenKind.Semicolon && parenDepth == 0 && current != TokenKind.CloseParen )
        {
            return true;
        }

        // ...and one that was ALREADY broken there is joined back, rather than kept the way a
        // newline inside parentheses otherwise is. Nobody writes `for ( ;;` with the `)` on its
        // own line on purpose; every instance is this formatter's own earlier output.
        if ( previous == TokenKind.Semicolon && current == TokenKind.CloseParen )
        {
            return false;
        }

        return newlinesBefore > 0;
    }

    /// <summary>
    /// Writes one line's indentation. Tabs are one character per level regardless of tab size,
    /// which is the point of using them; spaces multiply by the editor's width.
    /// </summary>
    private static void AppendIndent(StringBuilder output, int levels, FormatOptions options)
    {
        if ( levels <= 0 )
        {
            return;
        }

        if ( options.UseTabs )
        {
            output.Append('\t', levels);
            return;
        }

        output.Append(' ', levels * options.IndentWidth);
    }

    /// <summary>The intra-line separator between two adjacent tokens: a single space or nothing.</summary>
    private static string Separator(
        TokenKind beforePrevious, TokenKind previous, TokenKind current, bool insideCallParen, FormatOptions options)
    {
        // Parenthesis interior padding: "( x )", but "()" stays tight. A call's parentheses and a
        // control-flow/grouping pair each follow their own setting, so `if ( x )` with `foo(x)` --
        // a common mix in stock code -- is expressible.
        bool padParen = insideCallParen ? options.PadCallParens : options.PadParens;

        if ( previous == TokenKind.OpenParen )
        {
            return current == TokenKind.CloseParen || !padParen ? "" : " ";
        }

        if ( current == TokenKind.CloseParen )
        {
            return padParen ? " " : "";
        }

        // Bracket interiors are padded, matching parentheses: `a[ i ]`, `[[ ptr ]]`. Stock leans
        // the other way on indexes (19,175 tight against 4,686 padded), but this is a deliberate
        // override: one padding rule for every bracket reads better than an asymmetry nobody can
        // remember the direction of.
        //
        // Adjacent brackets stay tight, so a function pointer's `[[` and `]]` each read as one
        // token rather than as nested indexes, and an empty array stays `[]`.
        if ( previous == TokenKind.OpenBracket )
        {
            return current is TokenKind.OpenBracket or TokenKind.CloseBracket || !options.PadBrackets ? "" : " ";
        }

        if ( current == TokenKind.CloseBracket )
        {
            // A function pointer's `]]` is kept tight by TokenRoles; any other `] ]` closes nested
            // subscripts and is padded like every bracket: `a[ b[ c ] ]`.
            return options.PadBrackets ? " " : "";
        }

        // A '[' hugs its operand only when it SUBSCRIPTS one -- `a[ 0 ]`, `foo()[ 1 ]`. Opening an
        // array literal it is an operand in its own right and takes the spacing of one, or
        // `a = [];` would come out `a =[];`.
        if ( current == TokenKind.OpenBracket )
        {
            return EndsAnOperand(previous) ? "" : " ";
        }

        if ( NoSpaceAfter(previous) )
        {
            return "";
        }

        // A sign or address-of hugs its operand: `-150`, `&funcname`, `-( a )`. The same tokens are
        // binary when an operand precedes them (`a - b`, `flags & MASK`), and only then take the
        // space. Reported as `(- 150, - 1024, 304)` and `& funcname` in 2.0.0.
        if ( IsUnaryHere(beforePrevious, previous) )
        {
            return "";
        }

        if ( NoSpaceBefore(current) )
        {
            return "";
        }

        // A call/declaration '(' hugs its callee/name; a control-flow '(' follows
        // SpaceBeforeControlParen. Neither is affected by PadParens, which is about the INTERIOR.
        //
        // It only hugs something it could actually be CALLING. After an OPERATOR a '(' opens a
        // grouped subexpression and is an operand in its own right, or
        // `x = ( GetDvarString( "d" ) == "true" );` came out `x =( GetDvarString…`.
        //
        // Tested by what precedes rather than by what could be a callee: names are Identifier, but
        // so are `isdefined(`, `constructor(` and `destructor(`, which lex as keywords and would
        // lose their hug under an allow-list.
        if ( current == TokenKind.OpenParen )
        {
            // `if(`, `for(`, `while(` are a real style (13,600 tight against 23,645 spaced in stock),
            // so the keyword's space is its own setting. `return (` and `case (` are not keywords
            // opening a header and keep theirs regardless.
            if ( IsControlFlowKeyword(previous) )
            {
                return options.SpaceBeforeControlParen ? " " : "";
            }

            return IsGroupingParen(previous) ? " " : "";
        }

        return " ";
    }

    /// <summary>
    /// Whether a '(' after <paramref name="previous"/> opens a group rather than a call. `return ( … )`
    /// and `case ( … )` group rather than call, so they take the space that any other
    /// keyword-followed-by-paren would not.
    /// </summary>
    private static bool IsGroupingParen(TokenKind previous)
    {
        return IsControlFlowKeyword(previous)
            || IsBinaryOrAssignmentOperator(previous)
            || previous is TokenKind.Return or TokenKind.Case or TokenKind.Colon;
    }

    private static bool NoSpaceAfter(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Dot:
            case TokenKind.ScopeResolution:
            case TokenKind.Arrow:
            case TokenKind.Backslash:
            case TokenKind.Bang:
            case TokenKind.Tilde:
            case TokenKind.Hash:
            case TokenKind.Dollar:
                return true;
            default:
                return false;
        }
    }

    private static bool NoSpaceBefore(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Semicolon:
            case TokenKind.Comma:
            case TokenKind.Dot:
            case TokenKind.ScopeResolution:
            case TokenKind.Arrow:
            case TokenKind.Backslash:
            case TokenKind.PlusPlus:
            case TokenKind.MinusMinus:
            case TokenKind.Colon:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a token can end an operand, so that a following '[' is a subscript rather than the
    /// start of an array literal. Globals like `self` and `level` lex as Identifier.
    /// </summary>
    private static bool EndsAnOperand(TokenKind kind)
    {
        // `vararg` lexes as a keyword but is used as a variable, the array of extra arguments:
        // `vararg[ i ]`, `vararg.size`.
        return kind is TokenKind.Identifier
            or TokenKind.Vararg
            or TokenKind.Integer
            or TokenKind.Float
            or TokenKind.String
            or TokenKind.LocalizedString
            or TokenKind.HashString
            or TokenKind.CloseParen
            or TokenKind.CloseBracket;
    }

    /// <summary>
    /// Operators that must be followed by an operand, so a '(' or '[' after one opens a group or a
    /// literal rather than calling or subscripting what came before.
    ///
    /// Unary `!` and `~` are absent on purpose: they bind tight to their operand (`!( a )` is
    /// handled by <see cref="NoSpaceAfter"/> before this is ever consulted), as are `++` and `--`.
    /// </summary>
    private static bool IsBinaryOrAssignmentOperator(TokenKind kind)
    {
        switch ( kind )
        {
            case TokenKind.Assign:
            case TokenKind.Plus:
            case TokenKind.Minus:
            case TokenKind.Star:
            case TokenKind.Slash:
            case TokenKind.Percent:
            case TokenKind.Ampersand:
            case TokenKind.Pipe:
            case TokenKind.Caret:
            case TokenKind.LessThan:
            case TokenKind.GreaterThan:
            case TokenKind.EqualsEquals:
            case TokenKind.StrictEquals:
            case TokenKind.NotEquals:
            case TokenKind.StrictNotEquals:
            case TokenKind.LessThanEquals:
            case TokenKind.GreaterThanEquals:
            case TokenKind.LogicalAnd:
            case TokenKind.LogicalOr:
            case TokenKind.ShiftLeft:
            case TokenKind.ShiftRight:
            case TokenKind.PlusAssign:
            case TokenKind.MinusAssign:
            case TokenKind.StarAssign:
            case TokenKind.SlashAssign:
            case TokenKind.PercentAssign:
            case TokenKind.AmpersandAssign:
            case TokenKind.PipeAssign:
            case TokenKind.CaretAssign:
            case TokenKind.ShiftLeftAssign:
            case TokenKind.ShiftRightAssign:
            case TokenKind.QuestionMark:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="operatorKind"/> is a prefix operator in this position: minus, plus or ampersand
    /// with nothing before it that could be an operand. `)` and `]` end operands too, so `(a) - 1`
    /// and `a[ 0 ] - 1` stay binary.
    /// </summary>
    private static bool IsUnaryHere(TokenKind beforePrevious, TokenKind operatorKind)
    {
        return operatorKind is TokenKind.Minus or TokenKind.Plus or TokenKind.Ampersand
            && !EndsAnOperand(beforePrevious);
    }

    private static bool IsControlFlowKeyword(TokenKind kind)
    {
        return kind is TokenKind.If
            or TokenKind.While
            or TokenKind.For
            or TokenKind.Foreach
            or TokenKind.Switch;
    }

    /// <summary>Refuses formatting when the file has lexer (1xxx) or parser (3xxx) errors.</summary>
    private static bool HasSyntaxErrors(ParseResult result)
    {
        foreach ( Diagnostic diagnostic in result.AllDiagnostics )
        {
            if ( diagnostic.Severity != DiagnosticSeverity.Error )
            {
                continue;
            }

            if ( GscDiagnosticStages.IsLexing(diagnostic.Code) || GscDiagnosticStages.IsParsing(diagnostic.Code) )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether two texts lex to the same non-trivia token stream.</summary>
    private static bool SameTokens(string before, string after)
    {
        SourceText beforeText = SourceText.From(before);
        List<SignificantToken> beforeTokens = CollectSignificant(Lexer.Lex(beforeText).Tokens, beforeText);
        return TokenStreamMatches(beforeTokens, beforeText, after, new string?[beforeTokens.Count]);
    }

    /// <summary>
    /// Verifies the formatted output lexes to the same non-trivia token stream (kinds and
    /// exact text) as the input. This is the corruption guard — any mismatch aborts the edit.
    /// A token with a spelling in <paramref name="spellings"/> must come out with that spelling.
    /// </summary>
    private static bool TokenStreamMatches(
        List<SignificantToken> input, SourceText inputText, string formatted, string?[] spellings)
    {
        SourceText formattedText = SourceText.From(formatted);
        List<SignificantToken> output = CollectSignificant(Lexer.Lex(formattedText).Tokens, formattedText);

        if ( input.Count != output.Count )
        {
            return false;
        }

        for ( int index = 0; index < input.Count; index++ )
        {
            Token before = input[index].Token;
            Token after = output[index].Token;
            if ( before.Kind != after.Kind )
            {
                return false;
            }

            ReadOnlySpan<char> expected = spellings[index] is string spelling
                ? spelling.AsSpan()
                : before.GetText(inputText);
            if ( !expected.SequenceEqual(after.GetText(formattedText)) )
            {
                return false;
            }
        }

        return true;
    }
}
