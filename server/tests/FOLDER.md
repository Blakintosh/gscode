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

`CorpusPerfTests` holds both halves. The two parse sweeps time `ScriptAnalysis.Analyze` and split it
into lex/preprocess/parse/extract; `WorkspaceLints_WhereTheTimeGoes` times the CROSS-FILE LINTS,
which the parse sweeps do not touch at all and which cost roughly twenty times as much. It parses
outside the stopwatch so it measures lint cost only, and indexes fully first because two of the
heaviest rules stand down without a finished index — timing them against a partial one reports the
cheap half as the total. CoD4 and BO3 only, since each game measured pays a full index.

Add `-p:GscodeInstrumentation=true` for the per-lint breakdown; the `PerfTracker` scopes in
`WorkspaceLints` are `[Conditional]` and absent from an ordinary build.

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

**Build a workspace with `TestWorkspace.Build(profile, rawRoot, files)`.** It indexes through the
real `WorkspaceIndexer` with the dialect pinned, which is the part that must not be left to chance:
`GameProfile.Active` is BO3 in a test run, and under BO3 a keyword-less `is_coop()` is not a
declaration at all — so a workspace indexed for any other game comes back EMPTY rather than wrong,
and assertions about what it contains pass without proving anything. Two test files worked around
that by building `ScriptRecord`s by hand before the profile was a parameter.

**Analysis (the lints).** `IncludeUsageLintTests` 5026, the reported case plus every gate the Error
rests on and the transitive chain the corpus proved is required · `ArgumentCountLintTests` 5022/5023,
which declaration a call is judged against when a script function and a builtin share a name —
including the differently-SPELLED case that must not shadow · `UsingNotFoundLintTests` 5009 · `NamespaceUsageLintTests` 5000 ·
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
`DialectDependencyTests` · `DialectIncludeScopeTests` scope narrowing for BOTH dialects — the
`#include` merge graph, and the `#using` graph that separates two BO3 files sharing a `#namespace` ·
`ReferenceScopingTests` the same narrowing for reference COUNTS, including that a file declaring the
name in another namespace does not claim the reference ·
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

Every class here joins `GameProfileCollection`, which is what stops them running in parallel.
`GameProfile.Active` is process-global, so two games swept at once means one analysed under the
other's dialect — that once reported 861 of BO3's 980 scripts as unparseable. The rule had been
written in a comment long before anything enforced it. Within a game the per-file loop DOES run in
parallel, mirroring `WorkspaceIndexer`, and sweeps are memoized per game since four tests want the
same one.

The sweep also lifts every library gate and folds those findings into the same report marked
`[NOT SHOWN — rule gated off for this game]`. A gate exists so the EDITOR does not blame a user for
a hole in our data; the sweep is offline, so the same caution there only hides the holes from the
people who curate them. Findings the real pipeline already produced are not repeated, so a marked
line means "suppressed on this game" rather than "run twice".
- `CorpusTests` — BO3: nothing throws, lex/parse errors stay within budget, and the formatter
  preserves the token stream, is idempotent, produces line edits that reproduce the whole-document
  format, and neither loses nor invents a line when sorting directives or aligning. Also the gate on
  reference narrowing: every one of BO3's ~13,700 declared functions keeps its OWN definition after
  scoping. That is the failure mode narrowing has already produced once — an imports-only rule sent
  `combat.gsc`'s `main()` from 1,230 references to zero — and a unit test cannot catch it, because
  the mistake is always a real rule meeting a corpus shape nobody pictured.
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
