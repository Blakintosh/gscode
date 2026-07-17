using System.Collections.Immutable;
using GSCode.Core.Diagnostics;

namespace GSCode.Parser.Lexing;

/// <summary>
/// The lexer's complete output: every token including trivia (terminated by EndOfFile),
/// plus any lexical diagnostics. Lexing never fails — errors become Error tokens.
/// </summary>
public sealed record LexResult(ImmutableArray<Token> Tokens, ImmutableArray<Diagnostic> Diagnostics);
