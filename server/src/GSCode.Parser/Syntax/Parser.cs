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

        AddError(GscDiagnosticCode.ExpectedToken, Current.RootRange, display, DescribeCurrent());
        TextRange collapsed = new(Current.RootRange.Start, Current.RootRange.Start);
        return new PToken(kind, "", collapsed, Provenance.Root);
    }

    // --- Diagnostics ---

    private void AddError(GscDiagnosticCode code, TextRange range, params object[] arguments)
    {
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
