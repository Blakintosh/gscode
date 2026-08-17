using System.Collections.Immutable;
using GSCode.Core;
using GSCode.Core.Diagnostics;
using GSCode.Core.Text;

namespace GSCode.Parser.Lexing;

/// <summary>
/// Single forward scan over a source snapshot producing the flat token stream (trivia
/// included). Never throws: malformed input becomes Error tokens plus diagnostics.
/// </summary>
public sealed class Lexer
{
    private readonly SourceText _text;
    private readonly string _source;
    private readonly GameProfile _profile;
    private readonly ImmutableArray<Token>.Builder _tokens = ImmutableArray.CreateBuilder<Token>();
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

    private int _offset;

    // Where the last range lookup landed. Tokens are emitted in increasing offset order, so
    // resuming from here turns two binary searches per token into a short forward step.
    private int _lineHint;

    // Kind of the last non-trivia token, used to disambiguate %anim references from the
    // modulo operator. Null means start of file (which counts as an anim context).
    private TokenKind? _lastSignificantKind;

    private Lexer(SourceText text, GameProfile profile)
    {
        _text = text;
        _source = text.Text;
        _profile = profile;
    }

    /// <summary>
    /// Lexes the whole snapshot into tokens + diagnostics for a game's dialect. The profile only
    /// affects which words are keywords; it defaults to the active game.
    /// </summary>
    public static LexResult Lex(SourceText text, GameProfile? profile = null)
    {
        Lexer lexer = new(text, profile ?? GameProfile.Active);
        lexer.Run();
        return new LexResult(lexer._tokens.ToImmutable(), lexer._diagnostics.ToImmutable());
    }

    private void Run()
    {
        while ( _offset < _source.Length )
        {
            int startOffset = _offset;
            LexNext();

            // Fail-safe: the scan must always advance; a stall here would hang the server.
            if ( _offset == startOffset )
            {
                System.Diagnostics.Debug.Fail("Lexer did not advance — fix the token path that stalled.");
                _offset++;
            }
        }

        AddToken(TokenKind.EndOfFile, _source.Length, 0);
    }

    private void LexNext()
    {
        char current = _source[_offset];

        switch ( current )
        {
            case ' ':
            case '\t':
                LexWhitespaceRun();
                return;
            case '\r':
            case '\n':
                LexNewline();
                return;
            case '"':
                LexString(TokenKind.String, prefixLength: 0);
                return;
            case '/':
                LexSlash();
                return;
            case '#':
                LexHash();
                return;
            case '&':
                LexAmpersand();
                return;
            case '%':
                LexPercent();
                return;
            case '.':
                LexDot();
                return;
            case '|':
                AddOperator2(TokenKind.Pipe, '|', TokenKind.LogicalOr, '=', TokenKind.PipeAssign);
                return;
            case '^':
                AddOperator1(TokenKind.Caret, '=', TokenKind.CaretAssign);
                return;
            case '<':
                LexAngle('<', TokenKind.LessThan, TokenKind.LessThanEquals, TokenKind.ShiftLeft, TokenKind.ShiftLeftAssign);
                return;
            case '>':
                LexAngle('>', TokenKind.GreaterThan, TokenKind.GreaterThanEquals, TokenKind.ShiftRight, TokenKind.ShiftRightAssign);
                return;
            case '!':
                LexBangOrEquals(TokenKind.Bang, TokenKind.NotEquals, TokenKind.StrictNotEquals);
                return;
            case '=':
                LexBangOrEquals(TokenKind.Assign, TokenKind.EqualsEquals, TokenKind.StrictEquals);
                return;
            case '+':
                AddOperator2(TokenKind.Plus, '+', TokenKind.PlusPlus, '=', TokenKind.PlusAssign);
                return;
            case '-':
                LexMinus();
                return;
            case '*':
                AddOperator1(TokenKind.Star, '=', TokenKind.StarAssign);
                return;
            case ':':
                AddOperator2(TokenKind.Colon, ':', TokenKind.ScopeResolution, '\0', TokenKind.Colon);
                return;
            case '(':
                AddSimple(TokenKind.OpenParen);
                return;
            case ')':
                AddSimple(TokenKind.CloseParen);
                return;
            case '[':
                AddSimple(TokenKind.OpenBracket);
                return;
            case ']':
                AddSimple(TokenKind.CloseBracket);
                return;
            case '{':
                AddSimple(TokenKind.OpenBrace);
                return;
            case '}':
                AddSimple(TokenKind.CloseBrace);
                return;
            case ';':
                AddSimple(TokenKind.Semicolon);
                return;
            case ',':
                AddSimple(TokenKind.Comma);
                return;
            case '?':
                AddSimple(TokenKind.QuestionMark);
                return;
            case '~':
                AddSimple(TokenKind.Tilde);
                return;
            case '$':
                AddSimple(TokenKind.Dollar);
                return;
            case '\\':
                AddSimple(TokenKind.Backslash);
                return;
            default:
                break;
        }

        if ( IsWordStart(current) )
        {
            LexIdentifierOrKeyword();
            return;
        }

        if ( char.IsAsciiDigit(current) )
        {
            LexNumber();
            return;
        }

        // Nothing matched: consume one character as an error so the scan always advances.
        AddToken(TokenKind.Error, _offset, 1);
        AddDiagnostic(GscDiagnosticCode.UnexpectedCharacter, _offset, 1, current);
        _offset++;
    }

    // --- Trivia ---

    private void LexWhitespaceRun()
    {
        int start = _offset;
        while ( _offset < _source.Length && (_source[_offset] == ' ' || _source[_offset] == '\t') )
        {
            _offset++;
        }

        AddToken(TokenKind.Whitespace, start, _offset - start);
    }

    private void LexNewline()
    {
        int start = _offset;
        int length = 1;
        if ( _source[_offset] == '\r' && Peek(1) == '\n' )
        {
            length = 2;
        }

        _offset += length;
        AddToken(TokenKind.Newline, start, length);
    }

    // --- Words and numbers ---

    private void LexIdentifierOrKeyword()
    {
        int start = _offset;
        while ( _offset < _source.Length && IsWordChar(_source[_offset]) )
        {
            _offset++;
        }

        ReadOnlySpan<char> word = _source.AsSpan(start, _offset - start);
        if ( Keywords.TryMatchKeyword(word, _profile, out TokenKind keywordKind) )
        {
            AddToken(keywordKind, start, word.Length);
        }
        else
        {
            AddToken(TokenKind.Identifier, start, word.Length);
        }
    }

    private void LexNumber()
    {
        int start = _offset;

        // Hex: 0x followed by at least one hex digit.
        if ( _source[_offset] == '0' && (Peek(1) == 'x' || Peek(1) == 'X') && char.IsAsciiHexDigit(Peek(2)) )
        {
            _offset += 2;
            while ( _offset < _source.Length && char.IsAsciiHexDigit(_source[_offset]) )
            {
                _offset++;
            }

            AddToken(TokenKind.Hex, start, _offset - start);
            return;
        }

        while ( _offset < _source.Length && char.IsAsciiDigit(_source[_offset]) )
        {
            _offset++;
        }

        // A dot with a digit after it makes this a float; a bare trailing dot does not.
        bool isFloat = false;
        if ( _offset < _source.Length && _source[_offset] == '.' && char.IsAsciiDigit(Peek(1)) )
        {
            _offset++;
            while ( _offset < _source.Length && char.IsAsciiDigit(_source[_offset]) )
            {
                _offset++;
            }

            isFloat = true;
        }

        // An exponent makes it a float with or without a dot, so 1e5 is one just as 0.5e-09 is.
        if ( TryLexExponent() )
        {
            isFloat = true;
        }

        AddToken(isFloat ? TokenKind.Float : TokenKind.Integer, start, _offset - start);
    }

    /// <summary>
    /// Consumes an exponent suffix — <c>e</c>/<c>E</c>, an optional sign, then digits — and reports
    /// whether one was there. Stock scripts write vectors like
    /// <c>self.angles = ( 0.0, 0.0, 0.5e-09 );</c>, which did not lex at all without this.
    ///
    /// The whole suffix is validated BEFORE any of it is consumed, so an <c>e</c> that does not
    /// begin one is left for the next token. That matters because the lexer has no way back: were
    /// the marker eaten first, a hypothetical <c>1etc</c> would already have lost its <c>e</c> and
    /// the identifier after the number would silently change.
    /// </summary>
    private bool TryLexExponent()
    {
        if ( Peek(0) is not ('e' or 'E') )
        {
            return false;
        }

        int ahead = 1;
        if ( Peek(ahead) is '+' or '-' )
        {
            ahead++;
        }

        if ( !char.IsAsciiDigit(Peek(ahead)) )
        {
            return false;
        }

        _offset += ahead;
        while ( _offset < _source.Length && char.IsAsciiDigit(_source[_offset]) )
        {
            _offset++;
        }

        return true;
    }

    private void LexDot()
    {
        // ".5" is a float literal; "..." is the vararg ellipsis; otherwise a member dot.
        if ( char.IsAsciiDigit(Peek(1)) )
        {
            int start = _offset;
            _offset++;
            while ( _offset < _source.Length && char.IsAsciiDigit(_source[_offset]) )
            {
                _offset++;
            }

            // ".5e3" is as much a float as "0.5e3"; the leading zero is the only difference.
            TryLexExponent();

            AddToken(TokenKind.Float, start, _offset - start);
            return;
        }

        if ( Peek(1) == '.' && Peek(2) == '.' )
        {
            AddToken(TokenKind.Ellipsis, _offset, 3);
            _offset += 3;
            return;
        }

        AddSimple(TokenKind.Dot);
    }

    // --- Strings ---

    private void LexString(TokenKind kind, int prefixLength)
    {
        // The token starts at the prefix character (& or #) when present; the opening
        // quote sits right after it. Strings cannot span lines.
        int start = _offset;
        int cursor = start + prefixLength + 1;

        while ( cursor < _source.Length )
        {
            char current = _source[cursor];

            if ( current == '\r' || current == '\n' )
            {
                break;
            }

            if ( current == '\\' && cursor + 1 < _source.Length && !IsNewline(_source[cursor + 1]) )
            {
                cursor += 2;
                continue;
            }

            if ( current == '"' )
            {
                cursor++;
                _offset = cursor;
                AddToken(kind, start, cursor - start);
                return;
            }

            cursor++;
        }

        // Ran into a line break or end of file before the closing quote.
        _offset = cursor;
        AddToken(kind, start, cursor - start);
        AddDiagnostic(GscDiagnosticCode.UnterminatedString, start, cursor - start);
    }

    // --- Slash family: comments, doc blocks, dev blocks, division ---

    private void LexSlash()
    {
        char second = Peek(1);

        if ( second == '/' )
        {
            int start = _offset;
            while ( _offset < _source.Length && !IsNewline(_source[_offset]) )
            {
                _offset++;
            }

            AddToken(TokenKind.LineComment, start, _offset - start);
            return;
        }

        if ( second == '*' )
        {
            LexDelimitedTrivia(TokenKind.BlockComment, '*', GscDiagnosticCode.UnterminatedBlockComment);
            return;
        }

        if ( second == '@' )
        {
            LexDelimitedTrivia(TokenKind.DocComment, '@', GscDiagnosticCode.UnterminatedDocComment);
            return;
        }

        if ( second == '#' )
        {
            AddToken(TokenKind.DevBlockOpen, _offset, 2);
            _offset += 2;
            return;
        }

        if ( second == '=' )
        {
            AddToken(TokenKind.SlashAssign, _offset, 2);
            _offset += 2;
            return;
        }

        AddSimple(TokenKind.Slash);
    }

    /// <summary>Lexes /*...*/ or /@...@/ (both may span lines) into a single trivia token.</summary>
    private void LexDelimitedTrivia(TokenKind kind, char closerFirstChar, GscDiagnosticCode unterminatedCode)
    {
        int start = _offset;
        int cursor = start + 2;

        while ( cursor + 1 < _source.Length )
        {
            if ( _source[cursor] == closerFirstChar && _source[cursor + 1] == '/' )
            {
                cursor += 2;
                _offset = cursor;
                AddToken(kind, start, cursor - start);
                return;
            }

            cursor++;
        }

        _offset = _source.Length;
        AddToken(kind, start, _source.Length - start);
        AddDiagnostic(unterminatedCode, start, _source.Length - start);
    }

    // --- Hash family: dev-block close, hash strings, directives ---

    private void LexHash()
    {
        char second = Peek(1);

        if ( second == '/' )
        {
            AddToken(TokenKind.DevBlockClose, _offset, 2);
            _offset += 2;
            return;
        }

        if ( second == '"' && _profile.HasHashStrings )
        {
            // #"precached_string" is a Treyarch feature (BO1+). Where the dialect lacks it, this
            // falls through: '#' lexes as a bare Hash and the string on its own, so the parser
            // flags the stray '#' rather than silently accepting a foreign literal.
            LexString(TokenKind.HashString, prefixLength: 1);
            return;
        }

        // Whole-word directive match, so "#iffoo" is an unknown directive rather than
        // silently lexing as "#if" + "foo".
        int wordStart = _offset + 1;
        int wordEnd = wordStart;
        while ( wordEnd < _source.Length && IsWordChar(_source[wordEnd]) )
        {
            wordEnd++;
        }

        if ( wordEnd == wordStart )
        {
            AddSimple(TokenKind.Hash);
            return;
        }

        ReadOnlySpan<char> word = _source.AsSpan(wordStart, wordEnd - wordStart);
        int totalLength = wordEnd - _offset;

        if ( Keywords.TryMatchDirective(word, _profile, out TokenKind directiveKind) )
        {
            AddToken(directiveKind, _offset, totalLength);
        }
        else
        {
            AddToken(TokenKind.Error, _offset, totalLength);
            AddDiagnostic(GscDiagnosticCode.UnknownDirective, _offset, totalLength, _source.Substring(_offset, totalLength));
        }

        _offset = wordEnd;
    }

    // --- Ampersand family: &&, &=, &"istring", address-of & ---

    private void LexAmpersand()
    {
        char second = Peek(1);

        if ( second == '&' )
        {
            AddToken(TokenKind.LogicalAnd, _offset, 2);
            _offset += 2;
            return;
        }

        if ( second == '=' )
        {
            AddToken(TokenKind.AmpersandAssign, _offset, 2);
            _offset += 2;
            return;
        }

        if ( second == '"' )
        {
            LexString(TokenKind.LocalizedString, prefixLength: 1);
            return;
        }

        AddSimple(TokenKind.Ampersand);
    }

    // --- Percent family: %anim_ref vs modulo ---

    private void LexPercent()
    {
        // %word is an animation reference only where no operand can sit to the left:
        // after = ( , : ? return, or at the very start. Everywhere else % is modulo.
        int nameStart = AnimReferenceNameStart();

        if ( nameStart < _source.Length && IsWordStart(_source[nameStart]) && IsAnimReferenceContext() )
        {
            int start = _offset;
            _offset = nameStart;
            while ( _offset < _source.Length && IsWordChar(_source[_offset]) )
            {
                _offset++;
            }

            // One token covering the % AND the name, spaces included, so the reference has a single
            // range to hover, rename and report at. Consumers take the name with
            // <see cref="TokenFacts.AnimReferenceName"/> rather than slicing past the % themselves.
            AddToken(TokenKind.AnimReference, start, _offset - start);
            return;
        }

        AddOperator1(TokenKind.Percent, '=', TokenKind.PercentAssign);
    }

    /// <summary>
    /// Where the name of a <c>%</c> reference starts: past the <c>%</c> and past any spaces or tabs
    /// after it. The engine does not require the name to be joined to the sigil, and BO1's own
    /// maps\fullahead_anim.gsc writes <c>= % o_full_interstitial_01_camera;</c>, so the space is
    /// skipped here rather than turning that line into modulo and a stray identifier.
    ///
    /// Horizontal whitespace only. A <c>%</c> at the end of one line and a word at the start of the
    /// next is far more likely to be a modulo whose right operand was wrapped than an anim reference
    /// split across lines, and reading it as one token would swallow the line break with it.
    /// </summary>
    private int AnimReferenceNameStart()
    {
        int index = _offset + 1;

        while ( index < _source.Length && (_source[index] == ' ' || _source[index] == '\t') )
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Whether a <c>%word</c> here is an animation reference rather than modulo. Stated as the
    /// complement of the modulo case — <c>%</c> divides only when the token to its left can END an
    /// operand — because the set of operand-enders is small and closed, where the set of positions an
    /// anim reference may appear in is not. An allowlist of "after = ( , : ? return" misses every
    /// other operator, so real code like <c>if ( deathanim != %dying_crawl_death_v2 )</c> and
    /// <c>anim == %walk</c> lexed as modulo and failed to parse.
    /// </summary>
    private bool IsAnimReferenceContext()
    {
        return _lastSignificantKind is not (
            TokenKind.Identifier
            or TokenKind.Integer
            or TokenKind.Float
            or TokenKind.Hex
            or TokenKind.String
            or TokenKind.LocalizedString
            or TokenKind.HashString
            or TokenKind.AnimReference
            or TokenKind.CloseParen
            or TokenKind.CloseBracket
            or TokenKind.PlusPlus
            or TokenKind.MinusMinus
            or TokenKind.True
            or TokenKind.False
            or TokenKind.Undefined);
    }

    // --- Small operator helpers ---

    private void LexMinus()
    {
        char second = Peek(1);

        if ( second == '-' )
        {
            AddToken(TokenKind.MinusMinus, _offset, 2);
            _offset += 2;
        }
        else if ( second == '>' )
        {
            AddToken(TokenKind.Arrow, _offset, 2);
            _offset += 2;
        }
        else if ( second == '=' )
        {
            AddToken(TokenKind.MinusAssign, _offset, 2);
            _offset += 2;
        }
        else
        {
            AddSimple(TokenKind.Minus);
        }
    }

    /// <summary>Lexes = / == / === (or ! / != / !==) depending on how many '=' follow.</summary>
    private void LexBangOrEquals(TokenKind single, TokenKind withEquals, TokenKind strict)
    {
        if ( Peek(1) == '=' )
        {
            if ( Peek(2) == '=' )
            {
                AddToken(strict, _offset, 3);
                _offset += 3;
                return;
            }

            AddToken(withEquals, _offset, 2);
            _offset += 2;
            return;
        }

        AddSimple(single);
    }

    /// <summary>Lexes &lt; / &lt;= / &lt;&lt; / &lt;&lt;= (and the &gt; mirror).</summary>
    private void LexAngle(char angle, TokenKind single, TokenKind withEquals, TokenKind shift, TokenKind shiftAssign)
    {
        if ( Peek(1) == angle )
        {
            if ( Peek(2) == '=' )
            {
                AddToken(shiftAssign, _offset, 3);
                _offset += 3;
                return;
            }

            AddToken(shift, _offset, 2);
            _offset += 2;
            return;
        }

        if ( Peek(1) == '=' )
        {
            AddToken(withEquals, _offset, 2);
            _offset += 2;
            return;
        }

        AddSimple(single);
    }

    /// <summary>Single char, or a two-char form when '=' follows (e.g. ^ and ^=).</summary>
    private void AddOperator1(TokenKind single, char assignSecond, TokenKind assignKind)
    {
        if ( Peek(1) == assignSecond )
        {
            AddToken(assignKind, _offset, 2);
            _offset += 2;
            return;
        }

        AddSimple(single);
    }

    /// <summary>Single char, doubled form (e.g. ||), or assign form (e.g. |=).</summary>
    private void AddOperator2(TokenKind single, char doubledSecond, TokenKind doubledKind, char assignSecond, TokenKind assignKind)
    {
        char second = Peek(1);

        if ( second == doubledSecond )
        {
            AddToken(doubledKind, _offset, 2);
            _offset += 2;
            return;
        }

        if ( assignSecond != '\0' && second == assignSecond )
        {
            AddToken(assignKind, _offset, 2);
            _offset += 2;
            return;
        }

        AddSimple(single);
    }

    // --- Token/diagnostic plumbing ---

    private void AddSimple(TokenKind kind)
    {
        AddToken(kind, _offset, 1);
        _offset++;
    }

    private void AddToken(TokenKind kind, int start, int length)
    {
        // Both ends share the hint: the end is at or after the start, and the next token's start
        // is at or after this end, so the hint only ever moves forward across a whole file.
        Position startPosition = _text.GetPosition(start, ref _lineHint);
        Position endPosition = _text.GetPosition(start + length, ref _lineHint);

        TextRange range = new(startPosition, endPosition);
        Token token = new(kind, start, length, range);
        _tokens.Add(token);

        if ( !token.IsTrivia && kind != TokenKind.EndOfFile )
        {
            _lastSignificantKind = kind;
        }
    }

    private void AddDiagnostic(GscDiagnosticCode code, int start, int length, params object[] arguments)
    {
        // No hint here: a diagnostic is usually raised over the token just added, so its start is
        // BEHIND the hint and would only force the fallback. Rare enough that the search is free.
        TextRange range = new(_text.GetPosition(start), _text.GetPosition(start + length));
        _diagnostics.Add(Diagnostic.Create(range, DiagnosticSeverity.Error, code, arguments));
    }

    private char Peek(int lookahead)
    {
        int index = _offset + lookahead;
        if ( index >= _source.Length )
        {
            return '\0';
        }

        return _source[index];
    }

    private static bool IsWordStart(char character)
    {
        return char.IsAsciiLetter(character) || character == '_';
    }

    private static bool IsWordChar(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character == '_';
    }

    private static bool IsNewline(char character)
    {
        return character == '\r' || character == '\n';
    }
}
