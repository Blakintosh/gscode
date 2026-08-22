using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;
using GSCode.Parser.Lexing;
using GSCode.Parser.Preprocessing;
using GSCode.Parser.Syntax.Ast;

namespace GSCode.Parser.Syntax;

/// <summary>
/// Hand-written recursive descent over the preprocessed (trivia-free) token stream.
/// Panic-mode recovery: one diagnostic at the failure point, then silent skipping to a
/// sync token, so a garbled region never floods the file with errors. The tree always
/// covers the whole file. Split into partials: declarations / statements / expressions.
/// </summary>
public sealed partial class Parser
{
    private readonly ImmutableArray<PToken> _tokens;
    private readonly GameProfile _profile;
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
    private int _index;
    private int _nesting;
    private bool _abandonedNesting;

    /// <summary>
    /// Ceiling on how deeply one construct may nest, counted in GRAMMAR ENTRIES rather than in
    /// source levels: a <c>(</c> costs three (expression, ternary, unary), while a <c>{</c>, a
    /// <c>!</c>, a <c>?</c> or one link of an <c>a.b.c</c> / <c>1 + 1 + 1</c> chain costs one.
    ///
    /// Measured on a thread with the platform default 1 MB stack — what the server's thread-pool
    /// threads get — the cliff sits at 480 nested <c>(</c> (1,440 entries) and at ~1,430 links of a
    /// left-nested chain. The chain is the reason this counts TREE levels and not parser frames:
    /// the parser builds it with a loop and costs nothing, and it is
    /// <c>SymbolExtractor.WalkExpression</c> that then recurses one frame per link. Both shapes
    /// work out at ~0.7 KB of stack per entry, so 512 reaches about a third of the way to the cliff.
    ///
    /// A walker over the resulting tree inherits that bound only if its frames are no fatter than
    /// the ones measured here, and one is not: <see cref="AstPrinter"/> holds a switch over every
    /// node shape, and at this cap it clears a 1 MB stack in Release but not in Debug. It carries
    /// its own, lower ceiling for that reason — see <c>AstPrinter.MaxPrintDepth</c>. Anything else
    /// that recurses per level and is reachable from a lint should be measured, not assumed.
    ///
    /// Nothing hand-written comes near it: 512 entries is 170 nested parentheses, or 512 terms in
    /// one expression. What it prevents is a StackOverflowException, the one .NET failure that
    /// cannot be caught — it kills the whole server process, not the request.
    /// </summary>
    private const int MaxNestingDepth = 512;

    private Parser(ImmutableArray<PToken> tokens, GameProfile profile)
    {
        _tokens = tokens;
        _profile = profile;
    }

    /// <summary>Parses a preprocessed token stream into a syntax tree for the given game's dialect.</summary>
    public static ParseTree Parse(ImmutableArray<PToken> tokens, GameProfile profile)
    {
        Parser parser = new(tokens, profile);
        ScriptNode root = parser.ParseScript();
        return new ParseTree(root, parser._diagnostics.ToImmutable());
    }

    // --- Cursor ---

    private PToken Current
    {
        get { return _tokens[_index]; }
    }

    private TokenKind Kind
    {
        get { return _tokens[_index].Kind; }
    }

    /// <summary>Looks ahead without moving; clamps at EndOfFile.</summary>
    private PToken Peek(int lookahead)
    {
        int target = _index + lookahead;
        if ( target >= _tokens.Length )
        {
            return _tokens[^1];
        }

        return _tokens[target];
    }

    /// <summary>Consumes and returns the current token; parks at EndOfFile.</summary>
    private PToken Advance()
    {
        PToken token = _tokens[_index];
        if ( token.Kind != TokenKind.EndOfFile )
        {
            _index++;
        }

        return token;
    }

    /// <summary>Consumes the current token when it matches.</summary>
    private bool Match(TokenKind kind)
    {
        if ( Kind != kind )
        {
            return false;
        }

        Advance();
        return true;
    }

    /// <summary>
    /// Consumes a required token, or reports it missing and returns a zero-width
    /// placeholder at the current position so parsing can continue.
    /// </summary>
    private PToken Expect(TokenKind kind, string display)
    {
        if ( Kind == kind )
        {
            return Advance();
        }

        // A missing terminator is reported by the rule below rather than as a generic "expected X,
        // found Y", because for that one token both halves of that message are unhelpful.
        if ( kind == TokenKind.Semicolon )
        {
            ReportMissingSemicolon();
        }
        else
        {
            AddError(GscDiagnosticCode.ExpectedToken, Current.RootRange, display, DescribeCurrent());
        }

        TextRange collapsed = new(Current.RootRange.Start, Current.RootRange.Start);
        return new PToken(kind, "", collapsed, Provenance.Root);
    }

    /// <summary>
    /// Reports a statement that was never terminated, anchored at the END OF THAT STATEMENT rather
    /// than at the token that revealed it.
    ///
    /// The token that reveals it is the first token of the NEXT statement, which is the worst place
    /// to point: the reader is shown a line that is perfectly correct and told something is wrong
    /// with it. CoD4's own <c>animscripts\traverse\stairs_down.gsc</c> is the case — line 18 is
    /// <c>endPos = endnode.origin</c> with no semicolon, and the report landed on line 20 reading
    /// "Expected ';' but found 'horizontalDelta'", naming a variable that has nothing to do with it.
    ///
    /// The message drops the offending token for the same reason. Once the range is on the previous
    /// line, naming a token from a line further down actively contradicts where the reader is
    /// looking, and it was never information they needed: the fix is a semicolon, always in the same
    /// place, whatever came next.
    ///
    /// Deliberately not applied to the other tokens <see cref="Expect"/> asks for. Those are tokens
    /// the offending one was supposed to BE — a name after <c>function</c>, a <c>(</c> after that —
    /// and there the offender's own range is already exactly where the reader should look.
    /// </summary>
    private void ReportMissingSemicolon()
    {
        // Where the offending token SITS decides which of two different mistakes this is.
        //
        // On the SAME LINE it is almost never a forgotten terminator — nobody writes two statements
        // on one line and omits the separator — but a stray token. CoD4's
        // animscripts\traverse\stairs_up.gsc line 29 is the case: `endPos = self endnode.origin +
        // (0,0,1);` has a leftover `self` (its sibling stairs_down.gsc writes the same statement
        // without it). The fix is to delete a token, not to add one, so the old report is the right
        // one: point AT the offender and name it, because the reader can see it.
        //
        // On a LATER LINE the statement really was left unterminated, and then naming the offender is
        // worse than useless — it sends the reader to a line that is correct.
        if ( _index > 0 && Current.RootRange.Start.Line == _tokens[_index - 1].RootRange.End.Line )
        {
            AddError(GscDiagnosticCode.ExpectedToken, Current.RootRange, ";", DescribeCurrent());
            return;
        }

        AddError(GscDiagnosticCode.MissingSemicolon, MissingSemicolonRange());
    }

    private TextRange MissingSemicolonRange()
    {
        // Nothing precedes it, so there is no statement to anchor to.
        if ( _index == 0 )
        {
            return Current.RootRange;
        }

        // RootRange rather than Range so a statement whose last token came from a macro expansion
        // reports at the invocation the author wrote, not inside the macro body.
        TextRange previous = _tokens[_index - 1].RootRange;

        // One character wide rather than zero, because the diagnostic has to be FINDABLE: a
        // zero-width caret at the end of a line is the easiest thing in the panel to miss. A
        // single-line token gives up its last character; anything else — a multi-line token, or the
        // zero-width placeholder an earlier failure left behind — reports as it stands.
        if ( previous.End.Line == previous.Start.Line && previous.End.Character > previous.Start.Character )
        {
            return new TextRange(new Position(previous.End.Line, previous.End.Character - 1), previous.End);
        }

        return previous;
    }

    // --- Nesting ---

    /// <summary>
    /// Claims one level of nesting. False means the ceiling is reached and the caller must stop
    /// descending — see <see cref="StopAtNestingLimit"/>. Every successful claim is released by
    /// <see cref="ExitNesting"/>; nothing in the parser throws, so no path can skip the release.
    /// </summary>
    private bool EnterNesting()
    {
        if ( _nesting >= MaxNestingDepth )
        {
            return false;
        }

        _nesting++;
        return true;
    }

    /// <summary>Releases claimed levels. A loop that built one node per pass releases them together.</summary>
    private void ExitNesting(int levels = 1)
    {
        _nesting -= levels;

        // Back at declaration level: whatever was abandoned is fully unwound, so reporting resumes.
        if ( _nesting == 0 )
        {
            _abandonedNesting = false;
        }
    }

    /// <summary>
    /// Reports the ceiling and skips to the next statement boundary, so the caller resumes on
    /// something it can parse rather than walking straight back into the same nesting.
    /// </summary>
    private void StopAtNestingLimit()
    {
        // The message deliberately does not quote MaxNestingDepth: it counts grammar entries, not
        // anything the reader can count in their own file, and a number they cannot reconcile with
        // what they are looking at is worse than no number.
        AddError(GscDiagnosticCode.NestingTooDeep, Current.RootRange);
        _abandonedNesting = true;
        RecoverUnstuck();
    }

    /// <summary><see cref="StopAtNestingLimit"/> plus a placeholder covering the tokens it skipped.</summary>
    private ErrorNode AbandonNesting()
    {
        PToken start = Current;
        StopAtNestingLimit();
        return new ErrorNode(RangeFrom(start));
    }

    // --- Diagnostics ---

    private void AddError(GscDiagnosticCode code, TextRange range, params object[] arguments)
    {
        // An abandoned construct is reported once. Everything it drags down with it — a ')' that
        // will now never be found at each of the hundreds of levels being unwound — is a
        // consequence of that one report, and repeating it per level would bury it.
        if ( _abandonedNesting )
        {
            return;
        }

        _diagnostics.Add(Diagnostic.Create(range, DiagnosticSeverity.Error, code, arguments));
    }

    /// <summary>A readable name for the current token in error messages.</summary>
    private string DescribeCurrent()
    {
        if ( Kind == TokenKind.EndOfFile )
        {
            return "end of file";
        }

        return Current.Text;
    }

    // --- Range helpers ---

    /// <summary>Root-file range from a start token through the previously consumed token.</summary>
    private TextRange RangeFrom(PToken startToken)
    {
        TextRange start = startToken.RootRange;
        if ( _index == 0 )
        {
            return start;
        }

        TextRange end = _tokens[_index - 1].RootRange;
        if ( end.End < start.Start )
        {
            return start;
        }

        return new TextRange(start.Start, end.End);
    }
}
