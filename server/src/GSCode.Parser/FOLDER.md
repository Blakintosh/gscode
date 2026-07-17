# GSCode.Parser

The pure per-file analysis pipeline: lexer → preprocessor → parser → extraction.
A deterministic function library — no I/O except through injected providers, and no
LSP types anywhere.

*(Lexing lands in P1 (below); preprocessor in P2, parser in P3, extraction in P4.)*

## Lexing/TokenKind.cs

- `enum TokenKind` — every producible token kind: sentinels (EndOfFile, Error), trivia
  (Whitespace, Newline, LineComment, BlockComment, DocComment), literals (Identifier,
  Integer, Float, Hex, String, LocalizedString `&"..."`, HashString `#"..."`,
  AnimReference `%name`), case-insensitive keywords, case-sensitive preprocessor
  directives, dev-block delimiters, punctuation, and operators. Globals (`self`,
  `level`, …) deliberately lex as Identifier. `[[`/`]]` are NOT double-bracket kinds —
  the parser recognizes two ADJACENT brackets, so `a[b[1]]` lexes unambiguously.

## Lexing/Token.cs

- `readonly record struct Token(TokenKind Kind, int Start, int Length, TextRange Range)`
  — one token: UTF-16 offset span + precomputed line/character range. Tokens own no
  text; `GetText(SourceText)` returns a span view. `End` is one-past-last (half-open).
  `IsTrivia` marks the kinds the parser skips.

## Lexing/Keywords.cs

- `static class Keywords` — frozen lookup tables with span-based (allocation-free) lookup.
  - `TryMatchKeyword(span, out kind)` — case-INSENSITIVE (the language reference's own
    examples use `Function`/`Do`/`Break`).
  - `TryMatchDirective(span, out kind)` — case-SENSITIVE lowercase whole-word match for
    the word after `#` (engine convention).

## Lexing/Lexer.cs

- `sealed class Lexer` — single forward scan; `static Lex(SourceText) → LexResult`.
  Never throws: malformed input produces Error tokens + diagnostics, and a fail-safe in
  the main loop force-advances (with a debug assertion) if a token path ever stalls.
  Notable behaviors:
  - Strings cannot span lines; unterminated → token up to the break + diagnostic.
  - `/@ @/` doc blocks and `/* */` block comments span lines as single trivia tokens.
  - `/#` and `#/` lex as DevBlockOpen/DevBlockClose; dev-block content lexes normally.
  - Directive words match whole-word, so `#iffoo` is an unknown-directive error rather
    than `#if` + `foo`; bare `#` is a Hash token; unknown directives → Error + diagnostic.
  - `%word` is an AnimReference only where no operand can sit to its left (after
    `= ( , : ?` `return`, or at start of file — tracked via the last significant token);
    otherwise `%` is modulo.
  - `.5` lexes as Float; `1.` does not (Integer, then Dot); `0x` needs at least one hex
    digit; `...` is Ellipsis; `\` is a Backslash token (the preprocessor interprets it).

## Lexing/LexResult.cs

- `sealed record LexResult(ImmutableArray<Token> Tokens, ImmutableArray<Diagnostic> Diagnostics)`
  — the full stream (trivia included, EndOfFile-terminated) plus lexical diagnostics.

## Lexing/TokenCursor.cs

- `struct TokenCursor` — the parser's trivia-skipping view over the token array.
  `Current`/`Kind`/`Index`, `Advance()` (parks at EndOfFile), `Peek(n)` (looks ahead
  past trivia without moving).
