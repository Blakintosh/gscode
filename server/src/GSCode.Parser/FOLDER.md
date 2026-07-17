# GSCode.Parser

The pure per-file analysis pipeline: lexer → preprocessor → parser → extraction.
A deterministic function library — no I/O except through injected providers, and no
LSP types anywhere.

*(Lexing = P1, Preprocessing = P2 (both below); parser lands in P3, extraction in P4.)*

## Preprocessing/PToken.cs

- `readonly record struct Provenance(string? SourceFile, TextRange? RootSite, TextRange? DefinitionSite)`
  — where a preprocessed token really came from. All-null (`Provenance.Root`) = the root
  file as written. `SourceFile` = the file holding the token's true location (a .gsh for
  inserted tokens). `RootSite` = the root-file range to anchor diagnostics to (the
  #insert directive or macro invocation). `DefinitionSite` = the #define name range for
  macro-expanded tokens.
- `readonly record struct PToken(TokenKind Kind, string Text, TextRange Range, Provenance Provenance)`
  — one parse-stream token with materialized (interned) text, so the parser never juggles
  multiple SourceTexts. `RootRange` = RootSite ?? Range. Trivia never reaches this stream.

## Preprocessing/MacroTable.cs

- `sealed record MacroDefinition(Name, SourceFile, NameRange, Parameters, Body, Documentation)`
  — one #define: exact-case name, defining file (null = root), name-token range
  (go-to-def target), null Parameters for object-like, provenance-stamped body tokens,
  and any trailing same-line comment as documentation. `IsFunctionLike` derived.
- `sealed class MacroTable` — CASE-SENSITIVE (ordinal) name → definition map; macro
  names are the one case-sensitive identifier space. Redefinition silently replaces.

## Preprocessing/IInsertProvider.cs

- `sealed record InsertedFile(Path, Text, Tokens)` — a resolved, lexed insert target.
- `interface IInsertProvider` — supplies #insert targets; Workspace implements it over
  PathResolver + a lexed-GSH cache, keeping this project I/O-free.
- `sealed class NullInsertProvider` — always misses (isolated parses, tests).

## Preprocessing/PreprocessResult.cs

- `sealed record InsertEdge(RawPath, ResolvedPath, DirectiveRange, ContainingFile)` —
  one #insert dependency edge (ResolvedPath null on failure).
- `sealed record MacroInvocation(Name, SourceFile, Range, Definition)` — one macro use
  site; powers references/hover/signature help for macros.
- `sealed record PreprocessResult(Tokens, Macros, MacroInvocations, Inserts, DisabledRegions, Diagnostics)`
  — the full output: trivia-free EndOfFile-terminated parse stream, all macros, use
  sites, insert edges, root-file ranges disabled by inactive #if branches, diagnostics.

## Preprocessing/ConditionalEvaluator.cs

- `static class ConditionalEvaluator` — evaluates #if/#elif conditions over expanded
  tokens with the engine's exact grammar (verified against v1): `||` and `&&` chains,
  SINGLE ==/!= and relational applications, parens, INTEGER literals only — no
  defined(), no arithmetic. Unparseable → null → branch inactive; trailing junk ignored.

## Preprocessing/Preprocessor.cs

- `sealed class Preprocessor` — `static Process(rootFilePath, tokens, text, insertProvider, names)`.
  One linear pass per file; inserts recurse (depth cap 16 + active-path cycle set).
  - `#define`: keyword-or-identifier names; parameter list only when `(` is ADJACENT to
    the name; `\` continuation must immediately precede the line break (else diagnostic,
    backslash excluded); trailing comment captured as documentation.
  - `#insert`: path text sliced verbatim until `;` (line break → missing-semicolon
    diagnostic but the insert still proceeds); rooted/drive/`..` paths rejected; spliced
    tokens keep their own gsh-local ranges with SourceFile + RootSite provenance;
    diagnostics from inside inserts anchor at the root #insert site.
  - `#if/#elif/#else/#endif`: condition line macro-expanded then evaluated; first true
    branch processes, the rest record DisabledRegions (root file only, grey-out);
    inactive branches register nothing (defines/inserts inside them don't exist).
  - Macro expansion: exact-case lookup; keywords are candidates too (so `#define TRUE 1`
    works); function-like without `(` → diagnostic, no expansion; blank arguments expand
    to nothing; nested/argument expansion with a self-recursion guard; `__LINE__`
    (1-based), `__FILE__` (real path string), `FASTFILE` (`__fastfile__` placeholder).
  - Passes `#using`/`#namespace`/`#precache`/animtree directives through — those belong
    to the parser.

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
