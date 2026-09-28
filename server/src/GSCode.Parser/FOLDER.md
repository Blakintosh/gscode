# GSCode.Parser

The pure per-file analysis pipeline: lexer → preprocessor → parser → extraction.
A deterministic function library — no I/O except through injected providers, and no
LSP types anywhere.

## ParseResult.cs

- `sealed record ParseResult` — every stage's product (text, lexed, preprocessed, tree,
  extraction) plus the merged diagnostic list.
- `static class ScriptAnalysis` — THE per-file pipeline entry: `Analyze(path, language,
  text, insertProvider, names, profile?, headerCache?)` runs lex → preprocess → parse →
  extract, pure and synchronous. `profile` defaults to `GameProfile.Active`; `headerCache`
  is optional (isolated parses and most tests pass none). GSH lenient mode suppresses
  parse-stage (3xxx) diagnostics for injectable fragments while macros still extract
  fully. `LanguageFromPath` helper.

## Extraction/ExtractionResult.cs

- `sealed record ExtractionResult(Namespaces, Functions, Classes, References, Diagnostics, PathCalls)`
  — the extracted symbol surface the Workspace layer builds ScriptRecords from. `PathCalls`
  is the Infinity Ward path-qualified call sites (`maps\x::foo()`), kept alongside the
  namespace-less reference so go-to-definition can pin one to its file.

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
  against PrecacheAssetTypes, the classified reference list (definitions, calls with
  unqualified-under-current-namespace and sys::→builtin keying, address-of, class uses,
  field reads, plain writes and compound updates, class `var` declarations and the bare names that
  read them, macro def/use, literal references with the case rules), and /@ @/ doc
  association by line adjacency. A `FileScopeConstantNode`'s value (IW dialects) is
  walked for references the same way; it has no owning function, so the AssignmentSymbol
  builder used is scratch and discarded.
- A macro definition reference is emitted per `PreprocessResult.AllMacroDefinitions` entry,
  not per surviving `Macros` table entry — so a `#define` a later one in the same file (or
  a later `#insert`) shadows still gets a Definition reference at its OWN name, matching
  `DuplicateMacroDefinition`'s "the one being replaced."
- A parameter default's `DefaultValueText` is sliced from the ROOT file's source text at the
  default expression's range (falling back to `AstPrinter.Print` only for a function an
  `#insert` brings in, whose ranges collapse onto the insert site) — the text signature
  help, hover and export signatures show verbatim, so it has to read as written, not as
  AstPrinter's S-expression debug format.
- `_inStringConcatenation` is cleared (via `WalkWithoutConcatenation`) when descending into
  a call's target/arguments, an index's object/index, an arrow call's object/arguments, a
  `new`'s arguments, and a ternary's condition — every child that begins its OWN
  expression rather than continuing the enclosing `+` chain.

## Extraction/SemanticTokenType.cs

- `enum SemanticTokenType` (integer values are the LSP legend index contract) +
  `SemanticToken(Line, StartChar, Length, Type)`.

## Extraction/SemanticTokenBuilder.cs

- `static SemanticTokenBuilder.Build(ParseResult)` — ordered, non-overlapping semantic
  tokens: only IDENTIFIERS are classified, from the reference list
  (function/class/macro/property). Keywords, numbers, strings and comments are left
  entirely to the TextMate grammar — a semantic token can only add or repaint, never
  suppress a grammar scope, so classifying a purely lexical category either agrees (just
  changes the shade) or disagrees (the grammar's colour stands anyway), and either way it
  flickers on open before the server's tokens arrive. Unclassified identifiers are left to
  the grammar too.

## Extraction/FoldingRegions.cs

- `FoldingRegion(StartLine, EndLine, Kind)` + `static FoldingRegions.Compute(ParseResult)`
  — declarations/blocks/switches/dev blocks from the AST, multi-line comments and doc
  blocks from raw tokens, and case-insensitive nestable `/* region */`…`/* endregion */`
  user regions.

## Syntax/AstSearch.cs

- `static AstSearch` — `ChainAt(root, position)` (containing-node chain, outermost →
  innermost; the basis of selection ranges) and `ChildrenOf(node)` (full structural
  child enumeration).
- `ChildrenOf` returns `ChildEnumerable`, a STRUCT enumerable, and every caller is a `foreach` that
  binds to it by shape, so the walk allocates nothing. It was a `yield return` iterator, which
  allocated one state machine per node VISITED — and the tree is walked once per rule, by fifteen
  lints plus the reference, hint and typing passes, over corpus trees of a million nodes. Measured
  on bo3: a bare full-tree walk cost 128–145 ms through the iterator against 35–47 ms without it.
  A variant short-circuiting leaves before the type switch measured the same as the plain one, so
  the allocation was the cost and the thirty-case switch was not.
- `FileScopeConstantNode` yields its `Value` as its one child, same as `ParameterNode`'s default.

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
- `IncludeNode(Path, PathRange)` — `#include`, the Infinity Ward import that MERGES the
  included file's functions into scope (unlike `#using`'s namespace import); parsed
  identically to `UsingNode`, the merge semantics are a resolution concern elsewhere.
- `FileScopeConstantNode(NameToken, Value)` — `NAME = value;` outside any function (MW2
  onward; BO3 uses `#define` and rejects a bare top-level assignment).
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
  true/false/undefined, #animtree), `IdentifierNode`, `QualifiedNode` (ns::name),
  `PathQualifiedNode(Path, PathRange, NameToken)` — `maps\mp\_utility::foo`, the Infinity
  Ward path-qualified reference (call callee or, with no argument list, a function
  pointer); only appears when the profile has `HasInlinePathCalls`. The leading `::foo`
  local form is modelled with an empty `Path`.
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

- `sealed partial class Parser` — recursive descent over PTokens; `static Parse(tokens,
  profile)` → `ParseTree`. Panic-mode recovery: one diagnostic then silent skip to a sync
  token (declaration keywords / ';' / '}' / '#/'), always guaranteeing progress — including
  the case-body loop in `ParseSwitch` and `ParseDevBlockStatements`, which carry the same
  "did we advance?" guard `ParseBlock` does, as defense-in-depth against a future statement
  parser that stops guaranteeing it.
- `ParseUsing` and `ParseInclude` share `ParseDirectivePath` (path tokens up to `;`); they
  differ only in which AST node they build and which directive name a missing path names.
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
  A call result is also a legal method OBJECT, so the chain repeats — `ent getowner()
  stopuseturret()` — but only while the next callee stays on the line the previous call
  ended on. Across a line break a second callee is a missing semicolon far more often than
  a chain, and chaining there swallowed the 3014 entirely (CoD4 `stairs_down.gsc`).

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
  case label to compare it, so its recursion runs inside the server on whatever the parser
  produced. `IncludeNode` and `FileScopeConstantNode` print as `(include "path")` and
  `(const NAME value)`; every node shape has a case — the printer's `default:` fallback
  (`(?TypeName)`) exists only for a genuinely new shape, not as a standing gap.
- Descends at most `MaxPrintDepth = 128` levels and renders anything deeper as `(...)`. See the
  nesting note above for why the parser's own cap is not enough here.

## Preprocessing/PToken.cs

- `sealed record Provenance(string? SourceFile, TextRange? RootSite, TextRange? DefinitionSite)`
  — where a preprocessed token really came from. A CLASS (held by reference) rather than a
  struct, deliberately: it describes an expansion SITE, of which a file has a handful, but is
  carried by every token in the parse stream, and the overwhelming majority share the
  all-null answer — one shared `Provenance.Root` singleton beats copying three nullable
  fields into every token. All-null (`Provenance.Root`) = the root file as written.
  `SourceFile` = the file holding the token's true location (a .gsh for inserted tokens).
  `RootSite` = the root-file range to anchor diagnostics to (the #insert directive or
  macro invocation). `DefinitionSite` = the #define name range for macro-expanded tokens.
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
- `sealed record BuiltinExpansion(Name, Range, ExpandedText)` — one `__FUNCTION__`/`__FILE__`/
  `__LINE__` use and what it expanded to. By the time extraction runs the token is an
  ordinary literal with nothing marking where it came from, so hover reads this list
  (matched by range containment) to show the resolved value instead of a plain string's
  usual no-op.
- `sealed record PreprocessResult(Tokens, Macros, AllMacroDefinitions, MacroInvocations, BuiltinExpansions, Inserts, DisabledRegions, Diagnostics)`
  — the full output: trivia-free EndOfFile-terminated parse stream, all macros, use
  sites, insert edges, root-file ranges disabled by inactive #if branches, diagnostics.
  `Macros` is the SURVIVING definition per name (last one wins); `AllMacroDefinitions` is
  every `#define` actually parsed, in source order, including one a later redefinition
  shadowed — the definition-reference walk needs the latter, since the table alone has
  nothing to say about the loser of a redefinition.

## Preprocessing/ConditionalEvaluator.cs

- `static class ConditionalEvaluator` — evaluates #if/#elif conditions over expanded
  tokens with the engine's exact grammar (verified against v1): `||` and `&&` chains,
  SINGLE ==/!= and relational applications, parens, INTEGER literals only — no
  defined(), no arithmetic. Unparseable → null → branch inactive; trailing junk ignored.
- Paren nesting is capped at 64: it is a second recursive descent, over the condition's own
  tokens, so the parser's tree ceiling does not reach it. Past the cap the condition is simply
  unresolvable, which is a case the caller already handles.
- Integer literals parse with `NumberStyles.None` + `CultureInfo.InvariantCulture`, not the
  current thread's culture — matching `PragmaDirectives`' own `int.TryParse` elsewhere in
  this project.

## Preprocessing/Preprocessor.cs

- `sealed class Preprocessor` — `static Process(rootFilePath, tokens, text, insertProvider,
  names, profile?, headerCache?)`. One linear pass per file; inserts recurse (depth cap 16 +
  active-path cycle set).
  - The dialect is asked exactly twice, and everything else here is game-independent: the
    header-extension rule on `#insert`, and `ReportIfNoPreprocessor`, which raises `2016
    MacrosNotInDialect` when a `#define` or an `#if` member appears in a game without
    `GameProfile.HasMacros` — everything before BO3. Once per file, and the directive is then
    processed anyway so suppressing the code leaves a working file; the reasoning is on the code.
  - `#define`: keyword-or-identifier names; parameter list only when `(` is ADJACENT to
    the name; `\` continuation must immediately precede the line break (else diagnostic,
    backslash excluded); trailing comment captured as documentation. `2017
    DuplicateMacroDefinition` fires on a name redefined in one file, or shadowed across
    files (last one wins; the report names the one being replaced) — scoped per FRAME, not
    per file, so replaying a cached header's definitions checks the SAME question a fresh
    walk's `ParseDefine` would. `2018 DuplicateMacroParameter` fires on a repeated parameter
    name (added anyway, so the arity call sites are judged against still matches).
  - `#insert`: path text sliced verbatim until `;` (line break → missing-semicolon
    diagnostic but the insert still proceeds); rooted/drive/`..` paths rejected; spliced
    tokens keep their own gsh-local ranges with SourceFile + RootSite provenance;
    diagnostics from inside inserts anchor at the root #insert site.
  - `#if/#elif/#else/#endif`: condition line macro-expanded then evaluated; first true
    branch processes, the rest record DisabledRegions (root file only, grey-out);
    inactive branches register nothing (defines/inserts inside them don't exist). A second
    `#else`, or an `#elif` after a chain's `#else`, reports `UnexpectedConditionalDirective`
    rather than silently reading as one more (permanently inactive) branch.
  - Macro expansion: exact-case lookup; keywords are candidates too (so `#define TRUE 1`
    works); function-like without `(` → `MissingMacroArguments`, no expansion; a
    mismatched argument count → `WrongMacroArgumentCount` (checked for a nested call written
    inside another macro's body too, via `TryExpandBodyToken` — the same method
    `ExpandBody`'s own loop and `TryCollectBodyArguments`' argument scan both call, so a
    macro NAME used as a nested call's argument there expands the same way it would
    anywhere else); blank arguments expand to nothing; nested/argument expansion with a
    self-recursion guard (`_expansionStack`) plus a depth cap (`MaxMacroExpansionDepth =
    64`) on how deeply a call SITE may nest a function-like macro's own name
    (`F(F(F(…`) — unlike the self-recursion guard, that shape is bounded only by how much
    text the site writes, not by the file's macro count, and 20,000 levels of it overflowed
    the stack before the cap existed; `__LINE__` (1-based), `__FILE__` (always
    `_rootFilePath` — the compiled script, never `frame.SourceFile`, since `#insert`
    splices a header's own code as if written inline in the including file, so `__FILE__`
    inside one names the script it ends up part of, not the `.gsh` holding the text),
    `__FUNCTION__` (the enclosing `namespace::function`, found by scanning backward for
    the nearest `function` keyword and `#namespace` — a known simplification: it
    therefore finds nothing in a dialect without the `function` keyword, and can
    misattribute to an earlier function when used outside any function, both accepted
    since across the whole stock corpus this is written once), `FASTFILE`
    (`__fastfile__` placeholder). `__FUNCTION__`/`__FILE__`/`__LINE__` each record a
    `BuiltinExpansion` too, so hover on the literal text can show what it resolved to.
  - Passes `#using`/`#namespace`/`#precache`/animtree directives through — those belong
    to the parser.

## Lexing/TokenKind.cs

- `enum TokenKind` — every producible token kind: sentinels (EndOfFile, Error), trivia
  (Whitespace, Newline, LineComment, BlockComment, DocComment), literals (Identifier,
  Integer, Float, Hex, String, LocalizedString `&"..."`, HashString `#"..."`,
  AnimReference `%name`), case-insensitive keywords — including `ChildThread`/`Call`
  (MW2+, threaded vs. synchronous function-pointer calls) and `ThisThread` (the running
  thread as a value), each a keyword only where the dialect's keyword set lists it —
  case-sensitive preprocessor directives, dev-block delimiters, punctuation, and
  operators. Globals (`self`, `level`, …) deliberately lex as Identifier. `[[`/`]]` are
  NOT double-bracket kinds — the parser recognizes two ADJACENT brackets, so `a[b[1]]`
  lexes unambiguously.

## Lexing/Token.cs

- `readonly record struct Token(TokenKind Kind, int Start, int Length, TextRange Range)`
  — one token: UTF-16 offset span + precomputed line/character range. Tokens own no
  text; `GetText(SourceText)` returns a span view. `End` is one-past-last (half-open).
  `IsTrivia` marks the kinds the preprocessor drops on the way to the parse stream (the
  parser itself never sees a trivia token); the formatter and semantic tokens read the
  raw stream directly and use it to walk past whitespace and comments themselves.

## Lexing/TokenFacts.cs

- `static class TokenFacts` — `IsKeyword(kind)` (range-check over the contiguous keyword block
  in TokenKind) and `GetStaticText(kind)` (the canonical lexeme for fixed-text kinds —
  operators, punctuation, directives — or null when the source span must be sliced). Lets
  fixed-text tokens materialize their text without allocating.

## Lexing/GscIdentifier.cs

- `static class GscIdentifier` — what counts as an identifier, in one place: `WordChars` (a
  `SearchValues<char>`), `IsWordStart`, `IsIdentifier`. The lexer reads its rule from here, and so
  does anything else asking "is this a name" — a name the lexer would split in two is not a name.
  Deliberately ASCII: a Unicode-flavoured second copy had grown up in the server's code-action fixes
  and accepted names the lexer does not.

## Lexing/Keywords.cs

- `static class Keywords` — frozen lookup tables with span-based (allocation-free) lookup.
  - `TryMatchKeyword(span, out kind)` — case-INSENSITIVE (the language reference's own
    examples use `Function`/`Do`/`Break`).
  - `TryMatchDirective(span, out kind)` — case-SENSITIVE lowercase whole-word match for
    the word after `#` (engine convention).
  - Both have a `GameProfile` overload — `TryMatchKeyword(span, profile, out kind)` /
    `TryMatchDirective(span, profile, out kind)` — that matches only what the dialect
    actually has: a keyword the dialect lacks (`foreach` before MW2, `function`/`class` in
    the Infinity Ward games) or a directive from the wrong import family (`#include` vs.
    `#using`/`#namespace`/`#insert`/`#precache`) stays an ordinary word instead. BO3 has
    every keyword and directive, so its lexing is unchanged through either overload.

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
  - `%word` is an AnimReference only where no operand can sit to its left — stated as the
    COMPLEMENT of the modulo case (not after an identifier, literal, `)`/`]`, `++`/`--`,
    `true`/`false`/`undefined`, or another anim reference), since the set of
    operand-enders is small and closed where the set of positions an anim reference may
    appear in is not; an allowlist missed real code like `if ( deathanim != %walk )`.
    Otherwise `%` is modulo. Spaces and tabs may sit between the `%` and the name — BO1
    ships `= % o_full_interstitial_01_camera;` — and the token covers both, so consumers
    take the name with `TokenFacts.AnimReferenceName` rather than slicing past the `%`.
    A newline ends it: `%` at end of line is a wrapped modulo.
  - `.5` lexes as Float; `1.` does not (Integer, then Dot); `0x` needs at least one hex
    digit; `...` is Ellipsis; `\` is a Backslash token (the preprocessor interprets it).

## Lexing/LexResult.cs

- `sealed record LexResult(ImmutableArray<Token> Tokens, ImmutableArray<Diagnostic> Diagnostics)`
  — the full stream (trivia included, EndOfFile-terminated) plus lexical diagnostics.
