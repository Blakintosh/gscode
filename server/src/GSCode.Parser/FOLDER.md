# GSCode.Parser

The pure per-file analysis pipeline: lexer → preprocessor → parser → extraction.
A deterministic function library — no I/O except through injected providers, and no
LSP types anywhere.

## ParseResult.cs

- `sealed record ParseResult` — every stage's product (text, lexed, preprocessed, tree,
  extraction) plus the merged diagnostic list.
- `static class ScriptAnalysis` — THE per-file pipeline entry: `Analyze(path, language,
  text, insertProvider, names)` runs lex → preprocess → parse → extract, pure and
  synchronous. GSH lenient mode suppresses parse-stage (3xxx) diagnostics for
  injectable fragments while macros still extract fully. `LanguageFromPath` helper.

## Extraction/ExtractionResult.cs

- `sealed record ExtractionResult(Namespaces, Functions, Classes, References, Diagnostics)`
  — the extracted symbol surface the Workspace layer builds ScriptRecords from.

## Extraction/PragmaDirectives.cs

- `PragmaTarget` / `PragmaScope` / `PragmaDirective` — the parsed target, reach, and source line
  of an in-comment `#pragma disable|restore` directive.
- `static PragmaDirectives` — scans line, block, and documentation comments; accepts one
  diagnostic code, `gscode-<code>`, `all`, or `format`; and answers whether a diagnostic or the
  formatter is suppressed at a given line. State is source-ordered, so a later directive replaces
  the earlier state.
- Suppression is keyed on the CODE and never on a severity, so an Error is as suppressible as a
  Hint — wider than the C# pragma the spelling comes from, and why `warning` is not part of it.
  The C# form is still scanned as an undocumented alias, as is 1.5's `// gscode ignore`.
- 1.5's `// gscode ignore` / `/* gsc ignore */` is scanned as an alias, not a parallel mechanism:
  an `AllCodes` disable with `PragmaScope.OneLine` over the line below the comment's END line. A
  one-line directive neither reads nor writes the running disable/restore state.

## Extraction/SymbolExtractor.cs

- `sealed class SymbolExtractor.Extract(...)` — one AST walk producing: namespace spans
  (default = file stem; positional #namespace switching), FunctionSymbols with contained
  assignments (locals, any-owner fields, foreach variables, const), ClassSymbols
  (members/methods/ctor-dtor flags + parameter-rule diagnostics), #precache validation
  against PrecacheAssetTypes, plain-value default enforcement, the classified reference
  list (definitions, calls with unqualified-under-current-namespace and sys::→builtin
  keying, address-of, class uses, field accesses, macro def/use, literal references with
  the case rules), and /@ @/ doc association by line adjacency.

## Extraction/SemanticTokenType.cs

- `enum SemanticTokenType` (integer values are the LSP legend index contract) +
  `SemanticToken(Line, StartChar, Length, Type)`.

## Extraction/SemanticTokenBuilder.cs

- `static SemanticTokenBuilder.Build(ParseResult)` — ordered, non-overlapping semantic
  tokens: identifiers classified from the reference list (function/class/macro/property),
  keywords/numbers/strings/comments from the raw stream; multi-line comments split per
  line. Unclassified identifiers are left to the TextMate grammar.

## Extraction/FoldingRegions.cs

- `FoldingRegion(StartLine, EndLine, Kind)` + `static FoldingRegions.Compute(ParseResult)`
  — declarations/blocks/switches/dev blocks from the AST, multi-line comments and doc
  blocks from raw tokens, and case-insensitive nestable `/* region */`…`/* endregion */`
  user regions.

## Syntax/AstSearch.cs

- `static AstSearch` — `ChainAt(root, position)` (containing-node chain, outermost →
  innermost; the basis of selection ranges) and `ChildrenOf(node)` (full structural
  child enumeration).

## Syntax/Ast/AstNode.cs

- `abstract record AstNode(TextRange Range)` — base of every node. Range is in ROOT-file
  coordinates (inserted/expanded content collapses onto its root site); true locations
  of names come from their PTokens' provenance.
- `abstract record ExprNode` — base of expressions. `ErrorNode` — stands in for
  unparseable source so the tree always covers the file.

## Syntax/Ast/Declarations.cs

- `ScriptNode(Elements)` — every top-level element in source order (namespace state is
  positional). `UsingNode(Path, PathRange)`, `NamespaceNode(NameToken)`,
  `PrecacheNode(Arguments raw)`, `UsingAnimTreeNode(TreeNameToken)`.
- `FunctionNode(NameToken, IsPrivate, IsAutoexec, Parameters, HasVarargs, Body)` and
  `ParameterNode(NameToken, ByRef, DefaultValue)`.
- `ClassNode(NameToken, ParentToken, Members)` with `VarDeclNode`, `ConstructorNode`,
  `DestructorNode` (parameters parsed for P4 diagnostics — the spec forbids them).
- `DevBlockDeclNode(Declarations)` — top-level /# #/ wrapper.

## Syntax/Ast/Statements.cs

- `BlockNode`, `IfNode`, `WhileNode`, `DoWhileNode`, `ForNode`, `ForeachNode`
  (KeyToken null in the one-variable form), `SwitchNode` + `CaseGroupNode` (stacked
  labels share one body; null label = default), `ReturnNode`, `BreakNode`,
  `ContinueNode`, `WaitNode` (IsRealTime flags waitrealtime), `WaitTillFrameEndNode`,
  `ConstDeclNode`, `ExprStatementNode`, `DevBlockStmtNode`, `EmptyStatementNode`.

## Syntax/Ast/Expressions.cs

- Literals/names: `LiteralNode` (numbers, all three string kinds, anim refs,
  true/false/undefined, #animtree), `IdentifierNode`, `QualifiedNode` (ns::name).
- Structure: `ParenNode`, `VectorNode` ((x,y,z)), `ArrayLiteralNode` ([]),
  `BinaryNode`, `TernaryNode`, `PrefixNode` (! ~ - &), `PostfixNode` (++ --),
  `AssignmentNode`, `MemberNode` (.field), `IndexNode` ([i]).
- Calls: `PointerDerefNode` ([[p]]), `CallNode(Target?, IsThread, Callee, Arguments)` —
  one shape for every call form incl. method notation `ent foo()` and `thread` —
  `ArrowCallNode` ([[obj]]->m(args)), `NewNode` (new C()).

## Preprocessing/IHeaderMacroCache.cs

- `HeaderContribution` + `IHeaderMacroCache` — what one inserted header contributes: the macros it
  defines IN ORDER (a later `#define` of a name wins, so replaying the list reproduces exactly what a
  linear walk left behind) plus the insert edges from its own nested inserts. The interface lives
  here because the parser cannot reference the workspace that owns the instance; `InsertCache`
  implements it.
- The preprocessor stores a contribution ONLY when the header emitted no tokens, reported no
  diagnostic, invoked no macro and evaluated no `#if` — when its whole effect was to define things
  and that effect cannot have depended on the including file. Anything else is per-includer: emitted
  tokens belong in that file's stream, a diagnostic or invocation carries the invoking site's range,
  and a condition can name a macro the including file defined. The conditional case needs its own
  counter because an UNDEFINED name in a condition expands to nothing and leaves no invocation
  behind — the walk takes the `#else` with no trace that it chose. Those headers keep being walked,
  so the cache cannot change what anyone sees.
- Nested `#insert`s are re-resolved on a cache HIT, through the current root file's provider, and a
  mismatch with the recorded resolved path is treated as a miss. The key is the outer header's
  resolved path, but the value was produced by resolving the nested paths through whichever file
  walked it first: a mod overlaying only the nested header leaves the outer one resolving to the
  same file, so the key would match while the contribution belonged to raw's world. Re-resolving
  costs one path probe per nested edge, against a read, a lex and a walk to refuse the entry.

## Syntax/Parser.cs (+ .Declarations / .Statements / .Expressions partials)

- `sealed partial class Parser` — recursive descent over PTokens; `static Parse(tokens)
  → ParseTree`. Panic-mode recovery: one diagnostic then silent skip to a sync token
  (declaration keywords / ';' / '}' / '#/'), always guaranteeing progress.
- Nesting is capped at `MaxNestingDepth = 512` grammar entries (3015 NestingTooDeep, then a
  skip to the next statement boundary and silence until the parser is back at declaration
  level). Counted in `ParseExpression` / `ParseTernary` / `ParseUnary` / `ParseStatement` —
  between them every cycle in the grammar — and once per pass of the `ParseBinary` and
  `ParsePostfixChain` loops, because `1 + 1 + …` and `a.b.c…` cost the parser nothing but
  build a tree every walker then recurses down. The cap is therefore on TREE DEPTH, which is
  what makes SymbolExtractor, FoldingRegions and AstSearch safe by construction. AstPrinter is
  the exception and carries its own lower ceiling (`MaxPrintDepth = 128`, truncating to `(...)`):
  a bound in tree levels only transfers to a walker whose frames are no fatter than the ones it
  was measured against, and that one's are — at 512 it cleared a 1 MB stack in Release and died
  at 242 levels in Debug.
- Declarations: #using path joining (+ using-after-declaration diagnostic), #namespace,
  #precache (raw args for P4 validation), #using_animtree, functions (private/autoexec,
  defaults, &byRef, ... varargs), classes (single inheritance, var members, ctor/dtor,
  methods), top-level dev blocks.
- Statements: the full set incl. all wait forms, const, stacked switch labels,
  statement-level dev blocks, single-statement (braceless) bodies.
- Expressions: precedence climbing (|| < && < | < ^ < & < equality(incl. ===/!==) <
  relational < shifts < additive < multiplicative), right-assoc assignment + ternary,
  method-notation call chains (`ent [thread] callee(...)` where callee = identifier
  with '(' / ns::name / [[deref]] / call-shaped keyword like waittill/notify), pointer
  deref via two consecutive '[' in the trivia-free stream — spacing is irrelevant, so
  `[ [ ptr ] ]` == `[[ptr]]`, while `a[b[1]]` stays a nested index (its operand sits between
  the brackets and non-empty `[...]` array literals don't exist) — arrow calls, new,
  vectors, & function references. A `ParsePostfixChain` helper applies `.field`/`[index]`/
  `++`/`--` to a call result (a call used as a temporary), so both plain and method-notation
  calls can be indexed or member-accessed — e.g. `players[q] getangles()[1]`.

## Syntax/ParseTree.cs

- `sealed record ParseTree(ScriptNode Root, ImmutableArray<Diagnostic> Diagnostics)`.

## Syntax/PrecacheAssetTypes.cs

- `record PrecacheAssetType(Name, MinValues, MaxValues, Side)` + `enum PrecacheSide` +
  `static PrecacheAssetTypes` — the declarative asset-type table from the language
  reference (string-family types accept extra values). P4 validates PrecacheNodes
  against it; P8 completes from it.
- The `client_*` family (`client_fx`, `client_model`, `client_string`, `client_tagfxset`)
  is `PrecacheSide.Client`: `NamesFor(language)` keeps it out of GSC completion and
  `IsAvailableIn` drives `4006 ClientOnlyPrecacheType`, kept separate from
  `4000 UnknownPrecacheType` because a real type in the wrong world and a typo call for
  opposite responses. A `.gsh` is allowed everything, since which world inserts it is
  not knowable from the header.

## Syntax/AstPrinter.cs

- `static class AstPrinter.Print(node)` — deterministic S-expression rendering; the
  golden format for parser tests and a debugging aid. Not test-only: `CaseLabelLint` prints a
  case label to compare it, and `SymbolExtractor` prints a parameter's default value, so its
  recursion runs inside the server on whatever the parser produced.
- Descends at most `MaxPrintDepth = 128` levels and renders anything deeper as `(...)`. See the
  nesting note above for why the parser's own cap is not enough here.

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
  PathResolver + a lexed-GSH cache, keeping this project I/O-free. `TryResolveInsertPath` answers
  where a raw path lands without reading it, for the header-cache hit that re-checks nested inserts.
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
- Paren nesting is capped at 64: it is a second recursive descent, over the condition's own
  tokens, so the parser's tree ceiling does not reach it. Past the cap the condition is simply
  unresolvable, which is a case the caller already handles.

## Preprocessing/Preprocessor.cs

- `sealed class Preprocessor` — `static Process(rootFilePath, tokens, text, insertProvider, names)`.
  One linear pass per file; inserts recurse (depth cap 16 + active-path cycle set).
  - The dialect is asked exactly twice, and everything else here is game-independent: the
    header-extension rule on `#insert`, and `ReportIfNoPreprocessor`, which raises `2016
    MacrosNotInDialect` when a `#define` or an `#if` member appears in a game without
    `GameProfile.HasMacros` — everything before BO3. Once per file, and the directive is then
    processed anyway so suppressing the code leaves a working file; the reasoning is on the code.
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

## Lexing/TokenFacts.cs

- `static class TokenFacts` — `IsKeyword(kind)` (range-check over the contiguous keyword block
  in TokenKind) and `GetStaticText(kind)` (the canonical lexeme for fixed-text kinds —
  operators, punctuation, directives — or null when the source span must be sliced). Lets
  fixed-text tokens materialize their text without allocating.

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
