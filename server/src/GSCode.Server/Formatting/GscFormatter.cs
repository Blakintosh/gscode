using System.Collections.Immutable;
using System.Text;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser;
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
public static class GscFormatter
{

    /// <summary>A single text edit: the source range to replace and its replacement text.</summary>
    public readonly record struct FormatEdit(TextRange Range, string NewText);

    /// <summary>
    /// Formats the document and returns the MINIMAL edit that turns the original into the
    /// formatted text (common leading/trailing characters are trimmed), or null when there is
    /// nothing to change or formatting is refused. All three formatting requests (whole,
    /// range, on-type) share this so edits stay small and churn-free.
    /// </summary>
    public static FormatEdit? FormatMinimal(ParseResult result, FormatOptions? options = null)
    {
        string? formatted = Format(result, options);
        if ( formatted is null )
        {
            return null;
        }

        string original = result.Text.Text;
        if ( string.Equals(original, formatted, StringComparison.Ordinal) )
        {
            return null;
        }

        // Trim the common prefix and suffix so the edit spans only what actually changed.
        int start = 0;
        int maxPrefix = Math.Min(original.Length, formatted.Length);
        while ( start < maxPrefix && original[start] == formatted[start] )
        {
            start++;
        }

        int originalEnd = original.Length;
        int formattedEnd = formatted.Length;
        while ( originalEnd > start && formattedEnd > start && original[originalEnd - 1] == formatted[formattedEnd - 1] )
        {
            originalEnd--;
            formattedEnd--;
        }

        TextRange range = new(result.Text.GetPosition(start), result.Text.GetPosition(originalEnd));
        string replacement = formatted.Substring(start, formattedEnd - start);
        return new FormatEdit(range, replacement);
    }

    /// <summary>
    /// The formatting result as a set of PER-REGION edits — one small edit for each run of changed
    /// lines, with unchanged lines left out entirely.
    ///
    /// <see cref="FormatMinimal"/> returns a single edit spanning the first change to the last. For
    /// a document-wide reindent that is nearly the whole file, and an editor preserves the caret by
    /// mapping its offset through the edits — so a caret sitting inside that one big replacement has
    /// nowhere to map to and snaps to the edit's end. Diffing by LINES instead keeps every
    /// unchanged line, and the caret resting on one, untouched.
    ///
    /// The edits together reproduce <see cref="Format"/>'s output exactly, are ordered, and never
    /// overlap — a matched (unchanged) line always sits between two hunks — so they satisfy the
    /// LSP's requirements for a multi-edit response.
    /// </summary>
    public static ImmutableArray<FormatEdit> FormatMinimalEdits(ParseResult result, FormatOptions? options = null)
    {
        string? formatted = Format(result, options);
        if ( formatted is null )
        {
            return [];
        }

        string original = result.Text.Text;
        if ( string.Equals(original, formatted, StringComparison.Ordinal) )
        {
            return [];
        }

        return DiffByLines(result.Text, original, formatted);
    }

    /// <summary>Beyond this many changed lines on either side, one whole-region edit is used
    /// instead of a line diff. The line-diff matrix is quadratic, and a file this size being
    /// reindented wholesale is rare enough that the coarser edit is an acceptable fallback.</summary>
    private const int LineDiffLimit = 3000;

    private static ImmutableArray<FormatEdit> DiffByLines(SourceText text, string original, string formatted)
    {
        List<LineSpan> originalLines = SplitLines(original);
        List<string> formattedLines = [.. SplitLines(formatted).Select(static span => span.Text)];

        int originalCount = originalLines.Count;
        int formattedCount = formattedLines.Count;

        // Trim the runs of identical lines at the top and bottom; only the middle can differ.
        int lead = 0;
        while ( lead < originalCount && lead < formattedCount
            && string.Equals(originalLines[lead].Text, formattedLines[lead], StringComparison.Ordinal) )
        {
            lead++;
        }

        int tail = 0;
        while ( tail < originalCount - lead && tail < formattedCount - lead
            && string.Equals(
                originalLines[originalCount - 1 - tail].Text,
                formattedLines[formattedCount - 1 - tail],
                StringComparison.Ordinal) )
        {
            tail++;
        }

        int midOriginal = originalCount - tail - lead;
        int midFormatted = formattedCount - tail - lead;

        ImmutableArray<FormatEdit>.Builder edits = ImmutableArray.CreateBuilder<FormatEdit>();

        // One coarse edit when the middle is empty on a side (pure insertion or deletion) or too
        // large to diff. Correct either way; it just may span the caret.
        if ( midOriginal == 0 || midFormatted == 0 || midOriginal > LineDiffLimit || midFormatted > LineDiffLimit )
        {
            AddEdit(edits, text, originalLines, formattedLines, lead, originalCount - tail, lead, formattedCount - tail);
            return edits.ToImmutable();
        }

        // Longest common subsequence of the middle lines: the anchors that stay put, so the gaps
        // between them are the smallest set of edits that turns original into formatted.
        int[,] lcs = new int[midOriginal + 1, midFormatted + 1];
        for ( int i = midOriginal - 1; i >= 0; i-- )
        {
            for ( int j = midFormatted - 1; j >= 0; j-- )
            {
                if ( string.Equals(originalLines[lead + i].Text, formattedLines[lead + j], StringComparison.Ordinal) )
                {
                    lcs[i, j] = lcs[i + 1, j + 1] + 1;
                }
                else
                {
                    lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
        }

        int originalPos = 0;
        int formattedPos = 0;
        int walkI = 0;
        int walkJ = 0;
        while ( walkI < midOriginal && walkJ < midFormatted )
        {
            if ( string.Equals(originalLines[lead + walkI].Text, formattedLines[lead + walkJ], StringComparison.Ordinal) )
            {
                // A line that stays. Everything queued before it is one edit.
                if ( walkI > originalPos || walkJ > formattedPos )
                {
                    AddEdit(
                        edits, text, originalLines, formattedLines,
                        lead + originalPos, lead + walkI, lead + formattedPos, lead + walkJ);
                }

                originalPos = walkI + 1;
                formattedPos = walkJ + 1;
                walkI++;
                walkJ++;
            }
            else if ( lcs[walkI + 1, walkJ] >= lcs[walkI, walkJ + 1] )
            {
                walkI++;
            }
            else
            {
                walkJ++;
            }
        }

        // The final gap after the last anchor.
        if ( midOriginal > originalPos || midFormatted > formattedPos )
        {
            AddEdit(
                edits, text, originalLines, formattedLines,
                lead + originalPos, lead + midOriginal, lead + formattedPos, lead + midFormatted);
        }

        return edits.ToImmutable();
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
        edits.Add(new FormatEdit(range, replacement.ToString()));
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
    public static string? Format(ParseResult result, FormatOptions? requested = null)
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

        string formatted = Reflow(significant, result.Text, options);

        // Corruption guard: the reflow must preserve the exact non-trivia token stream.
        if ( !TokenStreamMatches(significant, result.Text, formatted) )
        {
            return null;
        }

        // Directive sorting runs AFTER the gate, because it deliberately moves tokens and would
        // trip it. It carries its own equality check instead -- see DirectiveSorter.
        if ( options.SortDirectives )
        {
            formatted = DirectiveSorter.Sort(formatted) ?? formatted;
        }

        // Consecutive alignment is also a post-pass: it changes only whitespace inside assignment
        // lines, so the gate above has already vouched for the tokens.
        if ( options.AlignConsecutive )
        {
            formatted = AssignmentAligner.Align(formatted);
        }

        return formatted;
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

    private static string Reflow(List<SignificantToken> significant, SourceText text, FormatOptions options)
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

        for ( int index = 0; index < significant.Count; index++ )
        {
            Token token = significant[index].Token;
            int newlinesBefore = significant[index].NewlinesBefore;

            // Closers dedent before this line's indent is computed. A dev block is deliberately
            // absent: `/# … #/` is a compile-time switch, not a scope -- the engine jumps over it
            // when dev script is off -- so indenting its body would imply a nesting that does not
            // exist. The stock scripts agree, 316 flush against 194 indented.
            if ( token.Kind == TokenKind.CloseBrace )
            {
                depth = Math.Max(0, depth - 1);
            }

            if ( token.Kind == TokenKind.CloseParen )
            {
                parenDepth = Math.Max(0, parenDepth - 1);
            }

            if ( token.Kind == TokenKind.CloseBrace && blocks.Count > 0 )
            {
                blocks.RemoveAt(blocks.Count - 1);
            }

            // A label sits at the block's own level, so it does not get its own case indent.
            bool isLabel = token.Kind is TokenKind.Case or TokenKind.Default;
            int caseIndents = OpenCaseIndents(blocks, excludeInnermost: isLabel);

            unbraced.BeforeToken(token.Kind);

            if ( index == 0 )
            {
                output.Append(token.GetText(text));
            }
            else
            {
                Token previous = significant[index - 1].Token;
                bool trailingComment = IsComment(token.Kind) && newlinesBefore == 0;

                if ( ShouldBreak(previous.Kind, token.Kind, newlinesBefore, trailingComment, parenDepth) )
                {
                    int blankLines = Math.Clamp(newlinesBefore - 1, 0, options.MaxBlankLines);
                    output.Append('\n', 1 + blankLines);
                    AppendIndent(output, depth + unbraced.PendingIndents + caseIndents, options);
                }
                else
                {
                    output.Append(Separator(previous.Kind, token.Kind, options));
                }

                output.Append(token.GetText(text));
            }

            // Openers indent everything that follows -- again, dev blocks excepted.
            if ( token.Kind == TokenKind.OpenBrace )
            {
                depth++;
                blocks.Add(new SwitchBlock { IsSwitch = switchHeaderSeen });
                switchHeaderSeen = false;
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

            if ( token.Kind == TokenKind.OpenParen )
            {
                parenDepth++;
            }

            unbraced.AfterToken(token.Kind);
        }

        output.Append('\n');
        return output.ToString();
    }

    /// <summary>One open brace: whether it is a switch body, and whether a case label is open in it.</summary>
    private sealed class SwitchBlock
    {
        public bool IsSwitch { get; init; }

        public bool CaseOpen { get; set; }
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

        /// <summary>Extra indent levels owed to unbraced bodies currently open.</summary>
        public int PendingIndents { get; private set; }

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
        }

        /// <summary>Called after the token is written, to arm or release the next body.</summary>
        public void AfterToken(TokenKind kind)
        {
            // A statement terminator ends every unbraced body stacked above it. `}` is reset
            // rather than decremented: a brace closing here means the body was braced after all,
            // or the tracker is out of step, and dropping to zero is the safe direction.
            // A ';' inside a header's parentheses separates the clauses of a `for` rather than
            // ending a statement. Treating it as a terminator tore down the header mid-flight, so
            // the ')' never armed the body and `for ( … )` with an unbraced statement under it was
            // left flat. Same root cause as the line-breaking rule in ShouldBreak.
            if ( kind == TokenKind.Semicolon && _expectingHeader && _headerParenDepth > 0 )
            {
                return;
            }

            if ( kind == TokenKind.Semicolon || kind == TokenKind.CloseBrace )
            {
                PendingIndents = 0;
                _expectingHeader = false;
                _awaitingBody = false;
                _awaitingBodyFromElse = false;
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
        TokenKind previous, TokenKind current, int newlinesBefore, bool trailingComment, int parenDepth)
    {
        if ( trailingComment )
        {
            return false;
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
        if ( previous == TokenKind.Semicolon && parenDepth == 0 )
        {
            return true;
        }

        return newlinesBefore > 0;
    }

    /// <summary>The intra-line separator between two adjacent tokens: a single space or nothing.</summary>
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
            output.Append('	', levels);
            return;
        }

        output.Append(' ', levels * options.IndentWidth);
    }

    private static string Separator(TokenKind previous, TokenKind current, FormatOptions options)
    {
        // Parenthesis interior padding: "( x )", but "()" stays tight.
        if ( previous == TokenKind.OpenParen )
        {
            return current == TokenKind.CloseParen || !options.PadParens ? "" : " ";
        }

        if ( current == TokenKind.CloseParen )
        {
            return options.PadParens ? " " : "";
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
            return current is TokenKind.OpenBracket or TokenKind.CloseBracket ? "" : " ";
        }

        if ( current == TokenKind.CloseBracket )
        {
            return previous == TokenKind.CloseBracket ? "" : " ";
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

        if ( NoSpaceBefore(current) )
        {
            return "";
        }

        // A call/declaration '(' hugs its callee/name; a control-flow '(' is padded.
        // Not affected by PadParens, which is about the INTERIOR: the tight form is `if (a)`,
        // never `if(a)`, so a control-flow keyword keeps its space either way.
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
            // `return ( … )` and `case ( … )` group rather than call, so they take the space that
            // any other keyword-followed-by-paren would not.
            bool grouping = IsControlFlowKeyword(previous)
                || IsBinaryOrAssignmentOperator(previous)
                || previous is TokenKind.Return or TokenKind.Case;

            return grouping ? " " : "";
        }

        return " ";
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
        return kind is TokenKind.Identifier
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

    private static bool IsControlFlowKeyword(TokenKind kind)
    {
        return kind is TokenKind.If
            or TokenKind.While
            or TokenKind.For
            or TokenKind.Foreach
            or TokenKind.Switch;
    }

    private static bool IsComment(TokenKind kind)
    {
        return kind is TokenKind.LineComment or TokenKind.BlockComment or TokenKind.DocComment;
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

            int code = (int)diagnostic.Code;
            bool lexError = code >= 1000 && code < 2000;
            bool parseError = code >= 3000 && code < 4000;
            if ( lexError || parseError )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verifies the formatted output lexes to the same non-trivia token stream (kinds and
    /// exact text) as the input. This is the corruption guard — any mismatch aborts the edit.
    /// </summary>
    private static bool TokenStreamMatches(List<SignificantToken> input, SourceText inputText, string formatted)
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

            if ( !before.GetText(inputText).SequenceEqual(after.GetText(formattedText)) )
            {
                return false;
            }
        }

        return true;
    }
}
