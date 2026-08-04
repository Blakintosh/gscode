# tests

Three suites, split by what they need rather than by what they cover: the parser tests need only a
string, the workspace tests need a database, and the server tests need the LSP layer or a real game
install. Written so a class can be found by keyword — search this file for the construct, not the
test directory for a name.

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
| `GSCODE_PERF_REPORT` | `Perf` tests | Overrides the directory for generated performance reports. |
| `GSCODE_SWEEP_REPORT` | corpus sweep tests | Overrides the directory for generated diagnostic-sweep reports. |

Every corpus variable names the game's raw folder **directly**. BO3 was once the exception, found
through `%TA_TOOLS_PATH%` with `share\raw` appended by the fixture; it now follows the same rule as
the rest, so there is one way to point a corpus at a game.

They are read at process start, so a terminal opened before they were set will not see them, and
setting one at user scope does NOT reach an already-running shell. Restart it, or pass them inline
for a single run — and watch the duration: a BO3 corpus run is minutes, so a `Category=Corpus` pass
finishing in milliseconds means every test no-opped.

Corpus tests carry `[Trait("Category", "Corpus")]`, so `--filter "Category=Corpus"` runs exactly the
suite that touches real game scripts.

There is a THIRD category, `Perf`, which is opted into separately: a perf run is a second pass over
every script, so it must not ride along with the diagnostic sweep. The everyday run therefore excludes
both — `--filter "Category!=Corpus&Category!=Perf"`, which is what CI uses:

| filter | what it runs |
|---|---|
| `Category!=Corpus&Category!=Perf` | the ordinary unit-test suite. Seconds, no game install needed |
| `Category=Corpus` | the diagnostic/formatter sweep over five games. Minutes |
| `Category=Perf` | per-file timing and the phase breakdown. Writes `temp/gscode-perf-<game>.html` |

`GSCODE_PERF_REPORT` overrides where the perf pages are written, matching `GSCODE_SWEEP_REPORT`. They
are deliberately different filenames, so a perf run never overwrites a diagnostic report.

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
`DialectImportTests` `#include` against `#using` · `ParserTerminationTests` that the parser always
terminates, under a timeout so a regression fails the suite instead of eating all available memory
on half-typed text.

**Extraction.** `ExtractionTests` symbols and references · `ClassExtractionTests` class members,
methods, and constructors · `DuplicateFunctionTests` ·
`LoopVariableTests` induction variables · `MacroDefaultParameterTests`, `MacroExpansionReferenceTests`
macro provenance · `SemanticTokenBuilderTests`, which pins what is deliberately NOT emitted (comments, keywords,
strings and numbers all belong to the grammar) · `TripleSlashScriptDocTests` ScriptDoc on the pre-BO3
games · `DialectResolutionTests` per-dialect symbol keys.

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
`FunctionResolutionLintTests` 5013/5014/5025, the split between a script miss and a builtin miss,
every condition that makes an Error defensible, and the keyword-from-a-later-dialect case that used
to be reported as a missing builtin · `ClassMethodLintTests` inherited and class-method
resolution diagnostics · `PathCallResolutionTests` a path call into a file the
distribution does not ship, reported once for the file rather than once per call ·
`UnreachableCodeLintTests` 5015 · `UnassignedVariableLintTests` 5016, and the ten shapes that are
NOT mistakes — each appeared in code that ships and works · `PragmaDirectiveTests` in-comment
suppression · `GameShapeDetectorTests` inferring a workspace's game · `WorkspaceDiagnosticBatchTests`
the batching and stored-diagnostic path used for indexed files.

**Api.** `ApiLoaderTests` the builtin library · `ClientApiTests` client-library derivation ·
`ObjectFieldsTests` engine fields, and that only
weapon fields are read-only · `RadiantKeyVisibilityTests` client-side keys — hidden from GSC, offered
to CSC — covering both how BO3 marks them (a `client` prefix) and how WaW/BO1 do (a second
`clientkeys.txt`) · `Cod4DataTests` CoD4's bundled data · `MacroExpansionPreviewTests`,
`MacroHoverProbeTests` macro hover.

**Completion.** `CompletionEngineTests` the surface at large · `SignatureEngineTests` signature help ·
`RealisticKeystrokeTests` completion mid-typing rather than at tidy boundaries ·
`ClassMethodCompletionTests` inherited and overriding methods · `ArrowSignatureTests` signatures
for unknown-receiver method calls · `DialectCompletionTests` that a dialect is offered only its
own keywords and global objects.

**Database and resolution.** `ScriptDatabaseTests` · `PathResolverTests` raw/mod resolution order ·
`DependencyRewriteTests` · `RawWriteGuardTests` refusing to write into a game install ·
`ClassGraphTests` incremental class-index updates · `MethodResolutionTests` inherited and
qualified method lookup · `MethodReferenceTests` class-method reference unions ·
`MacroNavigationTests`, `GshMacroLookupTests` macros across the three language worlds ·
`DialectDependencyTests`, `DialectIncludeScopeTests` the `#include` merge graph ·
`LocalDefinitionTests` go-to-definition on a local, which the shared reference index deliberately
does not carry · `RootDerivationTests` finding the game when nothing is configured ·
`ServerBuildIdentityTests` that two games can never share a cache.

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
- `ClassResolutionCorpusTests` — class inheritance and method calls across the shipped corpus,
  including cases where a receiver's concrete class is unknown.
- `BuiltinHarvestTests` — sweeps for calls resolving to neither a script function nor a known engine
  function and writes `harvest/<game>_missing_builtins.json` and `_missing_script_functions.json`.
  This is the curation input for the builtin libraries, ranked by how many files want a name.
- `CorpusDiagnosticSweepTests` — the whole lint pipeline over shipped scripts, where anything
  reported is either a real defect in the game's code or a false positive in ours.
- `ScriptDocCorpusTests` — ScriptDoc parsing against real doc blocks.
- `UnassignedVariableSweepTests` — measures 5016's false-positive RATE on shipped scripts rather
  than gating on a count. It went 2,742 reports on CoD4 alone down to 17 across all 7,309 scripts
  as each dialect fact was learned, so a jump means a gap in the rule's exclusions.

**Formatting.** `GscFormatterTests` the formatter at large · `FormatMinimalEditsTests` minimal edits ·
`StaleFormatEditTests` edits against a changed buffer · `UnbracedBodyFormattingTests`,
`UnbracedBodyShapeTests` braceless bodies · `ElseIfChainTests` · `OperatorSpacingTests`,
`BracketSpacingTests` · `ColumnAlignerTests`, `AssignmentAlignerTests` · `DirectiveSorterTests` ·
`FormatOptionsTests` the settings layer · `FormatPragmaTests` `#pragma warning disable format` ·
`GuidelineExampleTests` the examples in `FORMATTING.md`.

**Handlers.** `CodeActionHandlerTests` quick fixes · `DependentDiagnosticsTests` debounced
cross-file refreshes for other open documents · `CodeLensArgumentTests` the lens command payload,
which must be primitives so no serializer can case-mangle it · `CompletionResolveDataTests` ·
`DiagnosticsScopeTests` `gscode.diagnostics.scope` · `BuiltinAtTests` the builtin-under-cursor
request · `OnTypeBlockScopeTests` · `UntitledDocumentTests` documents with no path ·
`WorkspaceFoldersHandlerTests` · `IndexProgressNotifierTests`, `ServerStatusNotifierTests` ·
`DocumentSymbolNamelessTests` a half-typed declaration, which used to fail the WHOLE outline
request · `RenameScopeTests` what may be renamed, drawn on ownership rather than kind.

**Configuration and mapping.** `SettingsReachTheServerTests` that a setting actually arrives ·
`EffectiveSummaryTests` · `DiagnosticMappingTests` our diagnostics to LSP.
