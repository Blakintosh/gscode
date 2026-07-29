# tests

Three suites, split by what they need rather than by what they cover: the parser tests need only a
string, the workspace tests need a database, and the server tests need the LSP layer or a real game
install. Written so a class can be found by keyword — search this file for the construct, not the
test directory for a name.

`PORTING.md` records how every v1 test class was resolved; it is history, not an index.

## Environment variables

Every one is OPTIONAL. A test needing an absent corpus reports SKIPPED and passes, so the suite is
runnable by anyone without a game install — but a skipped corpus test proves nothing, so check the
output before trusting a green run.

| Variable | Used by | Points at |
|---|---|---|
| `GSCODE_CORPUS_COD4` | `GameCorpusFixture` | CoD4's raw script root, e.g. `…\CoD4-Mod-Tools\raw` |
| `GSCODE_CORPUS_WAW` | `GameCorpusFixture` | WaW's raw script root, e.g. `…\cod5-mod-tools\raw` |
| `GSCODE_CORPUS_MW2` | `GameCorpusFixture` | MW2's script root (no `raw` subfolder — the repo root itself) |
| `GSCODE_CORPUS_BO1` | `GameCorpusFixture` | BO1's raw script root, e.g. `…\Call of Duty Black Ops 42740\raw` |
| `GSCODE_CORPUS_BO3` | `CorpusFixture` | BO3's raw script root, e.g. `…\Call of Duty Black Ops III\share\raw` |
| `GSCODE_COD4_DOCS` | `tools/field-data` (not tests) | The CoD4 documentation pages. See `tools/field-data/FOLDER.md`. |
| `GSCODE_INSTRUMENTATION` | Compile-time constant, not an env var | Gates `PerfTracker.Report`; see `PERF.md`. |

Every corpus variable names the game's raw folder **directly**. BO3 was once the exception, found
through `%TA_TOOLS_PATH%` with `share\raw` appended by the fixture; it now follows the same rule as
the rest, so there is one way to point a corpus at a game.

They are read at process start, so a terminal opened before they were set will not see them, and
setting one at user scope does NOT reach an already-running shell. Restart it, or pass them inline
for a single run — and watch the duration: a BO3 corpus run is minutes, so a `Category=Corpus` pass
finishing in milliseconds means every test no-opped.

Corpus tests carry `[Trait("Category", "Corpus")]`, so `--filter "Category=Corpus"` runs exactly the
suite that touches real game scripts.

---

## GSCode.Parser.Tests

No workspace, no database — a source string in, tokens or a tree out.

**Lexing.** `LexerBasicsTests` tokens and spans · `LexerStringTests` strings, localized `&"…"`, hash
`#"…"` · `LexerTriviaTests` whitespace, comments, doc comments · `LexerDirectiveTests` `#`-directive
recognition · `LexerAnimContextTests` `%anim` references against modulo, the rule being that `%`
divides only when the token to its left can end an operand · `TokenCursorTests` trivia-skipping
cursor · `KeywordDialectTests` which words are keywords per game (`foreach`, `do`, `class`,
`childthread`, `call`, `const`) · `DialectLexingTests` per-dialect lexing at large.

**Preprocessing.** `DefineTests` object- and function-like macros · `BuiltinMacroTests` engine-defined
macros · `ConditionalTests` `#if`/`#elif`/`#else`/`#endif` · `InactiveBranchHintTests` the greyed-out
branch hint · `InsertTests` `#insert`, including cycles and depth limits.

**Syntax.** `DeclarationTests`, `StatementTests`, `ExpressionTests` the core grammar ·
`RecoveryTests` error recovery and resync · `LocalizedStringTests` · `StrayDevBlockCloseTests` an
unmatched `#/`, which stock scripts really do ship · `DialectDeclarationTests` keyword-less function
declarations · `DialectExpressionTests` path calls `maps\mp\_util::foo()`, `childthread`, `call [[ ]]`,
anim references, dev blocks holding whole functions, keywords as field names ·
`DialectImportTests` `#include` against `#using`.

**Extraction.** `ExtractionTests` symbols and references · `DuplicateFunctionTests` ·
`LoopVariableTests` induction variables · `MacroDefaultParameterTests`, `MacroExpansionReferenceTests`
macro provenance · `SemanticTokenBuilderTests` · `DialectResolutionTests` per-dialect symbol keys.

**Other.** `GameProfileTests` the profile registry — the 18-game lineage, which games are Supported
and Verified, keyword sets, and every capability flag · `NameTableTests` interning ·
`SourceTextTests`, `SurrogatePairPositionTests` offsets and positions across surrogate pairs.

---

## GSCode.Workspace.Tests

Needs a database. Fixtures use `FakeFileSystem`, so no game install is involved.

**Analysis (the lints).** `UsingNotFoundLintTests` 5009 · `NamespaceUsageLintTests` 5000 ·
`UnusedUsingLintTests` 5001 · `UnusedIncludeLintTests` 5012 · `PreferBooleanLiteralLintTests` 5002 ·
`PrivateAccessLintTests` 5003 · `ReadOnlyWriteLintTests` 5004/5005 · `DevBlockCallLintTests` 5006,
including which dev-only builtin candidates the stock corpus contradicts · `AmbiguousFunctionLintTests`
5007 · `UnusedLocalLintTests` 5008 · `CaseLabelLintTests` 5010/5011 ·
`FunctionResolutionLintTests` 5013/5014, the split between a script miss and a builtin miss and every
condition that makes an Error defensible · `PathCallResolutionTests` a path call into a file the
distribution does not ship, reported once for the file rather than once per call ·
`GameShapeDetectorTests` inferring a workspace's game.

**Api.** `ApiLoaderTests` the builtin library · `ObjectFieldsTests` engine fields, and that only
weapon fields are read-only · `RadiantKeyVisibilityTests` client-side keys — hidden from GSC, offered
to CSC — covering both how BO3 marks them (a `client` prefix) and how WaW/BO1 do (a second
`clientkeys.txt`) · `Cod4DataTests` CoD4's bundled data · `MacroExpansionPreviewTests`,
`MacroHoverProbeTests` macro hover.

**Completion.** `CompletionEngineTests` the surface at large · `SignatureEngineTests` signature help ·
`RealisticKeystrokeTests` completion mid-typing rather than at tidy boundaries ·
`DialectCompletionTests` that a dialect is offered only its own keywords and global objects.

**Database and resolution.** `ScriptDatabaseTests` · `PathResolverTests` raw/mod resolution order ·
`DependencyRewriteTests` · `RawWriteGuardTests` refusing to write into a game install ·
`MacroNavigationTests`, `GshMacroLookupTests` macros across the three language worlds ·
`DialectDependencyTests`, `DialectIncludeScopeTests` the `#include` merge graph.

**Cache, documents, typing.** `SqliteCacheTests`, `DeleteDatabaseTests` · `StaleAnalysisTests` edits
racing analysis · `WatchedFileUpdaterTests` · `FlowTyperTests`, `TypeFlowConvergenceTests` local type
inference and that the walk terminates.

---

## GSCode.Server.Tests

The LSP layer, the formatter, and the real-corpus sweeps.

**Corpus** (all `Category=Corpus`, all no-op without their game). `CorpusFixture` locates BO3 via
`GSCODE_CORPUS_BO3`; `GameCorpusFixture` locates the others via `GSCODE_CORPUS_<GAME>`, built from
each profile's `ShortName`.
- `CorpusTests` — BO3: nothing throws, lex/parse errors stay within budget, and the formatter
  preserves the token stream, is idempotent, produces line edits that reproduce the whole-document
  format, and neither loses nor invents a line when sorting directives or aligning.
- `GameCorpusTests` — the same three properties per game, and the evidence behind
  `GameProfile.Verified`.
- `BuiltinHarvestTests` — sweeps for calls resolving to neither a script function nor a known engine
  function and writes `harvest/<game>_missing_builtins.json` and `_missing_script_functions.json`.
  This is the curation input for the builtin libraries, ranked by how many files want a name.
- `CorpusDiagnosticSweepTests` — the whole lint pipeline over shipped scripts, where anything
  reported is either a real defect in the game's code or a false positive in ours.
- `ScriptDocCorpusTests` — ScriptDoc parsing against real doc blocks.

**Formatting.** `GscFormatterTests` the formatter at large · `FormatMinimalEditsTests` minimal edits ·
`StaleFormatEditTests` edits against a changed buffer · `UnbracedBodyFormattingTests`,
`UnbracedBodyShapeTests` braceless bodies · `ElseIfChainTests` · `OperatorSpacingTests`,
`BracketSpacingTests` · `ColumnAlignerTests`, `AssignmentAlignerTests` · `DirectiveSorterTests` ·
`FormatOptionsTests` the settings layer · `GuidelineExampleTests` the examples in `FORMATTING.md`.

**Handlers.** `CodeActionHandlerTests` quick fixes · `CodeLensArgumentTests` the lens command payload,
which must be primitives so no serializer can case-mangle it · `CompletionResolveDataTests` ·
`DiagnosticsScopeTests` `gscode.diagnostics.scope` · `BuiltinAtTests` the builtin-under-cursor
request · `OnTypeBlockScopeTests` · `UntitledDocumentTests` documents with no path ·
`WorkspaceFoldersHandlerTests` · `IndexProgressNotifierTests`, `ServerStatusNotifierTests`.

**Configuration and mapping.** `SettingsReachTheServerTests` that a setting actually arrives ·
`EffectiveSummaryTests` · `DiagnosticMappingTests` our diagnostics to LSP.
