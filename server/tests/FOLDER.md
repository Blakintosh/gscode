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
| `Category=Scale` | generated 10K–50K-file workspaces. No-op unless `GSCODE_SCALE_SIZES` is set; see below |

`CorpusPerfTests` holds every half. The two parse sweeps time `ScriptAnalysis.Analyze` and split it
into lex/preprocess/parse/extract; `WorkspaceLints_WhereTheTimeGoes` times the CROSS-FILE LINTS,
which the parse sweeps do not touch at all and which cost roughly twenty times as much. It parses
outside the stopwatch so it measures lint cost only, and indexes fully first because two of the
heaviest rules stand down without a finished index — timing them against a partial one reports the
cheap half as the total. CoD4 and BO3 only, since each game measured pays a full index.

`Completion_WhereTheTimeGoes` times `CompletionEngine.Complete` at ten evenly spaced call sites per
file, plus one FILE-SCOPE position — one report row per REQUEST, since the question is whether a
keystroke is answered in time and a per-file sum answers nothing anybody waits for. The call sites
come from the extraction's call references and every one of those is inside a body, so until the
file-scope sample was added this sweep could not see the other arm at all. It also prints how many
ENTRIES came back, and that line is what makes the timings admissible: `Complete` has ~10 arms and
only the statement-scope one queries the store, so a sample that quietly hit the cheap arms would
report fast completions and have measured nothing. Adds BO1 to the usual CoD4/BO3 pair, because every query on this path reads
the record store and BO1 is the only corpus large enough to show whether store size drives the cost.
It does not — see PERF.md, which also records why the quadratic found here was left alone.

`ColdIndex_WhereTheTimeGoes` is the third to name BO1, for the unrelated reason that its raw tree is
160,382 files and enumeration cost is not a function of script count.

Add `-p:GscodeInstrumentation=true` for the per-lint breakdown; the `PerfTracker` scopes in
`WorkspaceLints` are `[Conditional]` and absent from an ordinary build.

`GSCODE_PERF_REPORT` overrides where the perf pages are written, matching `GSCODE_SWEEP_REPORT`. They
are deliberately different filenames, so a perf run never overwrites a diagnostic report.

`Category=Scale` is the fourth, and costs minutes per size. `ScaleCorpusFixture` builds a workspace
far larger than any game ships out of the real scripts — the game's raw tree stays raw, and a
generated workspace folder holds copies, one subfolder per copy, `#namespace` suffixed per copy on
bo3, about three quarters of them at or above the median file size — and reuses it once marked
complete. `ScalePerfTests` (with its `.Handlers` and `.Lookups` partials) measures each size in a fresh database:
cold and warm start, dropped cache writes, retained memory, the compaction pause, completion (call
site, literal, field), one file's lint pass with a per-rule breakdown, the full-mode sweep, and
CodeLens / references / rename over 50 stock and 50 copied files, and the narrower lookups
(directive path completion, a changed header's dependents, a rename's directive edits, class
lookups, header macro references, `ArgumentCountLint` alone), each printed beside its budget and
written to `temp/gscode-scale.md`. The per-request rows must stay FLAT as the workspace grows; one
that climbs means something on that path walks the store. PERF.md's scale section holds the numbers.

| Variable | Meaning |
|---|---|
| `GSCODE_SCALE_SIZES` | Totals to build and measure, e.g. `10000,25000,50000`. Unset = no-op |
| `GSCODE_SCALE_GAMES` | Which games, default `bo3`; each needs its `GSCODE_CORPUS_<GAME>` |
| `GSCODE_SCALE_ROOT` | Where the generated workspaces go, default `%TEMP%\gscode-scale` |

---

## GSCode.Parser.Tests

No workspace, no database — a source string in, tokens or a tree out.

**Lexing.** `LexerBasicsTests` tokens and spans · `LexerStringTests` strings, localized `&"…"`, hash
`#"…"` · `LexerTriviaTests` whitespace, comments, doc comments · `LexerDirectiveTests` `#`-directive
recognition · `LexerAnimContextTests` `%anim` references against modulo, the rule being that `%`
divides only when the token to its left can end an operand · `TokenFactsTests` that every keyword
kind sits inside the range `IsKeyword` checks · `TokenWidthTests` the two token structs' byte width,
which decides how long a script can be before its token array lands on the large-object heap ·
`KeywordDialectTests` which words are keywords per game (`foreach`, `do`, `class`,
`childthread`, `call`, `const`) · `DialectLexingTests` per-dialect lexing at large.

**Preprocessing.** `DefineTests` object- and function-like macros · `BuiltinMacroTests` engine-defined
macros · `ConditionalTests` `#if`/`#elif`/`#else`/`#endif` · `InactiveBranchHintTests` the greyed-out
branch hint · `InsertTests` `#insert`, including cycles and depth limits · `MacrosNotInDialectTests`
that `#define` and the `#if` chain are BO3's alone, that the directive is reported once per file and
still EXPANDS, and that an orphan `#endif` gets the dialect answer rather than "unexpected" ·
`DuplicateMacroTests` a duplicate macro parameter and a duplicate definition — two rules, not one ·
`HeaderContributionCacheTests` a header's replayed definitions, in pairs of runs sharing one cache ·
`MacroExpansionDepthTests` the cap on argument-collection recursion ·
`NestedMacroBodyExpansionTests` a macro name written as a nested call's argument inside another
macro's body. `PreprocessTestHelper` is an in-memory insert provider.

**Syntax.** `DeclarationTests`, `StatementTests`, `ExpressionTests` the core grammar ·
`RecoveryTests` error recovery and resync · `LocalizedStringTests` · `StrayDevBlockCloseTests` an
unmatched `#/`, which stock scripts really do ship · `DialectDeclarationTests` keyword-less function
declarations · `DialectExpressionTests` path calls `maps\mp\_util::foo()`, `childthread`, `call [[ ]]`,
anim references, dev blocks holding whole functions, keywords as field names ·
`DialectImportTests` `#include` against `#using` · `ParserTerminationTests` that the parser always
terminates, under a timeout so a regression fails the suite instead of eating all available memory
on half-typed text · `ParserDepthTests` the nesting ceiling, since the alternative is an uncatchable
`StackOverflowException` · `ParseCancellationTests` a superseded analysis stops rather than
finishing · `SyntaxDiagnosticTests` what syntax alone decides (assignment for comparison, a
parameter twice, `#insert` of a non-header) · `ChildEnumerationTests` what `AstSearch.ChildrenOf`
yields and in what order — every AST walk in the server goes through it — and that a whole-tree
walk allocates nothing. `ParserTestHelper`/`LexTestHelper` run a snippet through the pipeline.

**Extraction.** `ExtractionTests` symbols and references · `ClassExtractionTests` class members,
methods, and constructors · `DuplicateFunctionTests` ·
`LoopVariableTests` induction variables · `WaittillBindingTests` that `waittill`'s trailing arguments
are BOUND, not read — the only place those names come into existence ·
`MacroDefaultParameterTests`, `MacroExpansionReferenceTests`
macro provenance · `SemanticTokenBuilderTests`, which pins what is deliberately NOT emitted (comments, keywords,
strings and numbers all belong to the grammar) · `TripleSlashScriptDocTests` ScriptDoc on the pre-BO3
games · `DialectResolutionTests` per-dialect symbol keys · `MacroBodyReferenceTests` a macro name
inside another macro's `#define` body recorded as a `MacroUse` · `MacroGeneratedDeclarationTests` a
declaration a macro produces anchored at the INVOCATION, not inside the `#define` ·
`FieldBindingTests` the two halves of a field write, WHERE (`FieldWrite`) and WHAT was put there
(`FieldBinding`), including the right-hand sides that must record no binding at all.

**Other.** `GameProfileTests` the profile registry — the 18-game lineage, which games are Supported
and Verified, keyword sets, and every capability flag · `NameTableTests` interning ·
`SourceTextTests`, `SurrogatePairPositionTests` offsets and positions across surrogate pairs ·
`ScrValueTests` the union type lattice, written against v1.5's mistakes · `ScrOperatorsTests`
operator semantics over it, the vector rows especially · `DiagnosticMessagesTests` that every code
has a template.

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
including the differently-SPELLED case that must not shadow · `UsingNotFoundLintTests` 5009, both
import spellings, since an unresolvable `#include` reported nothing at all and silenced 5026 with it ·
`NamespaceUsageLintTests` 5000, including the merge dialect where the rule is unsatisfiable and says nothing ·
`UnusedUsingLintTests` 5001 · `UnusedIncludeLintTests` 5012 · `PreferBooleanLiteralLintTests` 5002 ·
`PrivateAccessLintTests` 5003 · `ReadOnlyWriteLintTests` 5004/5005 ·
`GlobalObjectWriteLintTests` 5035, bare name versus write-THROUGH, and the dialect where `world` is
an ordinary local name · `DevBlockCallLintTests` 5006,
including which dev-only builtin candidates the stock corpus contradicts · `AmbiguousFunctionLintTests`
5007 · `UnusedLocalLintTests` 5008 · `CaseLabelLintTests` 5010/5011 ·
`FunctionResolutionLintTests` 5013/5014/5025, the split between a script miss and a builtin miss,
every condition that makes an Error defensible, and the keyword-from-a-later-dialect case that used
to be reported as a missing builtin · `ClassMethodLintTests` inherited and class-method
resolution diagnostics · `PathCallResolutionTests` a path call into a file the
distribution does not ship, reported once for the file rather than once per call ·
`UnreachableCodeLintTests` 5015 · `UnassignedVariableLintTests` 5016, and the ten shapes that are
NOT mistakes — each appeared in code that ships and works · `PragmaDirectiveTests` in-comment
suppression · `IgnoreCommentTests` 1.5's `// gscode ignore` alias, which suppresses the line below
it alone · `GameShapeDetectorTests` inferring a workspace's game · `WorkspaceDiagnosticBatchTests`
the batching and stored-diagnostic path used for indexed files · `ArithmeticLintTests` 5031, the
literal-zero case only · `ConstDeclarationLintTests` 5029/5030 and what counts as "known" ·
`ExpressionStatementLintTests` 5032, reported only when NOTHING in the expression has an effect ·
`ThreadedResultLintTests` 5028 · `TypeMismatchLintTests` 5033/5034, which report nothing on any
corpus, so these controls are all that stands between them and being silently broken ·
`ClassCycleLintTests` 5021, including a cycle crossing a file boundary · `UnusedBindingLintTests`
5020, and `waittillmatch`'s trailing argument being a READ. `NodeLintHarness` runs one per-node rule
the way `NodeLintPass` runs all nine.

**Api.** `ApiLoaderTests` the builtin library · `ClientApiTests` client-library derivation ·
`ObjectFieldsTests` engine fields, and that only
weapon fields are read-only · `RadiantKeyVisibilityTests` client-side keys — hidden from GSC, offered
to CSC — covering both how BO3 marks them (a `client` prefix) and how WaW/BO1 do (a second
`clientkeys.txt`) · `Cod4DataTests` CoD4's bundled data · `MacroExpansionPreviewTests`,
`MacroHoverProbeTests` macro hover · `ApiTypeParsingTests` the declared types the loader used to
flatten and drop · `BundledDataTests` that every data file a profile CLAIMS to ship exists ·
`EmpiricalBuiltinCoverageTests` the WaW/BO1 libraries against their corpus harvests ·
`KeywordDocsTests` the two halves keyword hover needs · `MacroArgumentSpanTests` the positions behind
macro parameter-name inlay hints · `StockScriptsTests` a locked list file reads as empty.

**Completion.** `CompletionEngineTests` the surface at large · `SignatureEngineTests` signature help ·
`RealisticKeystrokeTests` completion mid-typing rather than at tidy boundaries ·
`ClassMethodCompletionTests` inherited and overriding methods · `ArrowSignatureTests` signatures
for unknown-receiver method calls · `DialectCompletionTests` that a dialect is offered only its
own keywords, global objects, snippets and DIRECTIVES — including the reported case end to end, a
`#` at file scope under CoD4 returning exactly `#include` and `#using_animtree`, that `#animtree` is
a body position in every game, and that a CoD4 file-scope list carries no macro rows for a
preprocessor that game does not have · `MergeDialectScopeTests` what a merge dialect puts in scope:
this file and its `#include`s, never every file sharing a name stem ·
`UnimportedFunctionCompletionTests` which functions auto-import offers and how each dialect family
spells the call: qualified under `#using`, bare under `#include`.

`LocalScopeCompletionTests` also holds the FILE-SCOPE cases: the macros an `#insert`ed header
supplies offered outside a body, the functions in scope and the expression atoms a macro
invocation's arguments need, and the engine globals still withheld there. The punctuation pair
(no terminator at file scope, no parentheses on a function pointer) lives with the other call
punctuation cases in `CompletionEngineTests`. `FakeInserts` — the `IInsertProvider` that serves a
header's text — is shared between them, since both features have to be asked about a macro that
lives in a `.gsh` rather than the file under test.

`SignatureEngineTests` holds the MACRO cases beside the function and builtin ones: parameter names
and the active argument, the reported case of an `#insert`ed macro invoked at file scope, and the
three positions that must NOT answer with a macro — an object-like one, a name whose case does not
match, and a qualified `namespace::NAME(`.

**Database and resolution.** `ScriptDatabaseTests` · `PathResolverTests` raw/mod resolution order,
plus the resolution memo (a hit and a miss both cost nothing the second time; a watched
create/delete forces a re-probe) · `ImportResolutionProbeCostTests` the filesystem-probe count
behind `#using`/`#include` — confirms the memo's fix, and that the cold, root-count-multiplied
cost it cannot remove is still exactly what it should be ·
`WorkspaceIndexerOwnershipTests` an open document's committed record survives a concurrent index
pass untouched, and the disk content still reaches the cache ·
`WorkspaceIndexerConcurrencyTests` two `IndexAsync` calls on one indexer never interleave ·
`DependencyRewriteTests` · `RawWriteGuardTests` refusing to write into a game install ·
`ClassGraphTests` incremental class-index updates · `MethodResolutionTests` inherited and
qualified method lookup · `MethodReferenceTests` class-method reference unions ·
`MacroNavigationTests`, `GshMacroLookupTests` macros across the three language worlds ·
`DialectDependencyTests` · `DialectIncludeScopeTests` scope narrowing for BOTH dialects — the
`#include` merge graph, and the `#using` graph that separates two BO3 files sharing a `#namespace` ·
`ReferenceScopingTests` the same narrowing for reference COUNTS, including that a file declaring the
name in another namespace does not claim the reference ·
`LocalDefinitionTests` go-to-definition on a local, which the shared reference index deliberately
does not carry · `LocalReferencesTests` the occurrence list behind find-references, highlight and
rename on a variable — what counts as a WRITE, and the names a per-function answer must refuse
(globals, IW file-scope constants, class members) · `RootDerivationTests` finding the game when
nothing is configured ·
`ServerBuildIdentityTests` that two games can never share a cache · `ExportSignatureTests` what an
edit must NOT fan out on · `LocalSemanticTokensTests` parameters and locals for highlighting ·
`OverlayShadowingReferenceTests` a mod overlay hiding the raw copy it replaces, in references and
completion · `DeclarationIndexTests` the bare-name, `(namespace, name)` and dev-only lists across
edits and removals · `RelativePathIndexTests` · `VocabularyIndexTests` literals and fields, visible
files only, never a macro body's · `BoundedLookupTests` `LookupFunctions`' per-record shadowing
against the old two-pass rule, and that a capped answer is the front of the full one ·
`IncludeScopeLookupTests` the one-name include-scope lookup against the whole-scope list, across an
overlay that re-declares a name, one that drops it, and a sibling mod ·
`SameFileReferenceTests` asking the reference query for ONE file against asking it wide and keeping
that file, across a mod overlay, a sibling mod and a file nothing references ·
`DirectiveIndexTests`, `PathTreeIndexTests`, `QualifiedClassLookupTests`, `VisibleClassesTests` and
`HeaderReferenceIndexTests` each keep the walk an index replaced as a reference and require its
answer, across overlays, sibling mods, edits and removals ·
`ReferencesReachingTests` the scoped reference query's overlay case, which stock corpora cannot
exercise · `InsertCacheTests` two headers differing only by case on Linux ·
`PhysicalFileSystemTests` that the byte-level read returns what `File.ReadAllText` does.
`CountingFileSystem` counts probes so a test can assert on work done.

**Indexing.** `CacheRowPruningTests` a file deleted between sessions loses its cache row ·
`HeaderChangeReachTests` everything a watched header's change must reach, not one hop ·
`IndexerInsertCacheTests` the indexer and the editor sharing ONE header cache ·
`RestoredContextIdentityTests` a restored record gets this session's context and relative path ·
`RestoredInsertResolutionTests` a header created or deleted between sessions reaching the restored
scripts that insert it · `WatcherRaceTests` a slow index commit never clobbering a fresher
watched-file one.

**Cache, documents, typing.** `SqliteCacheTests`, `DeleteDatabaseTests` · `RecordSerializerTests` the
binary record layout: every settable property of every record type pinned, a fully populated round
trip, and corrupt, truncated and old-format blobs reading as null · `StaleAnalysisTests` edits
racing analysis, including that `AnalyzeSnapshot` stamps the WINNING version on a superseded
caller's own result rather than its own · `WatchedFileUpdaterTests` · `FlowTyperTests`, `TypeFlowConvergenceTests` local type
inference and that the walk terminates · `ValueIdentityTests` the two facts the `ScrType` projection
cannot hold — which class an instance is, which function a pointer holds — including the guard that
no OTHER label moved when display stopped going through the projection · `DocumentStoreTests` a
second `didOpen` for an open path · `AnalysisCancellationTests` a superseded analysis stops ·
`HeaderEditRefreshTests` an open document going stale when a header it inserts changes ·
`ParameterTypesTests` parameter types read off the arguments callers pass · `ScriptTypesTests` the
per-node query surface a transpiler consumes · `TypeCoverageTests` the expression and statement
forms the pass used not to reach · `VectorArithmeticTests` vector arithmetic end to end.
`TestParallelism` serializes this assembly's collections, since one class switches the active
dialect.

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
- `LintBudgetTests` — the only TIMING gate in the suite: no cross-file lint may cost more than 40%
  of `AnalysisTiming.DebounceMilliseconds` on its worst single file, and the whole pass no more than
  25% at p99. bo3 and cod4, since the two dialects run different rules — the include rules are free
  on the namespace dialect and among the most expensive on the merge one. It times through
  `LintTimings` rather than `PerfTracker`, because the tracker is `[Conditional]` and a gate that
  reads it would assert over an empty set in an ordinary build and pass by measuring nothing. The
  bound is deliberately several times the measurement; PERF.md's per-lint budget section holds the
  numbers and the reasoning. Writes `temp/gscode-lint-budget-<game>.html`.
- `ClassResolutionCorpusTests` — class inheritance and method calls across the shipped corpus,
  including cases where a receiver's concrete class is unknown. Every lookup passes the context of
  the record the symbol came from, never the literal `"raw"`: the fixture indexes the mods folder
  beside the raw one, so a class declared in an installed mod is only findable in that mod's
  context.
- `BuiltinHarvestTests` — sweeps for calls resolving to neither a script function nor a known engine
  function and writes `harvest/<game>_missing_builtins.json` and `_missing_script_functions.json`.
  This is the curation input for the builtin libraries, ranked by how many files want a name.
- `CorpusDiagnosticSweepTests` — the whole lint pipeline over shipped scripts, where anything
  reported is either a real defect in the game's code or a false positive in ours.
- `ScriptDocCorpusTests` — ScriptDoc parsing against real doc blocks.
- `UnassignedVariableSweepTests` — measures 5016's false-positive RATE on shipped scripts rather
  than gating on a count. It went 2,742 reports on CoD4 alone down to 17 across all 7,309 scripts
  as each dialect fact was learned, so a jump means a gap in the rule's exclusions.
- `RecordFormatCorpusTests` — every record a real bo3 and cod4 index produces survives the cache's
  binary layout with its JSON unchanged. The layout is positional, so a dropped field restores a
  record a little wrong rather than failing.
- `ReferenceScopeCorpusTests` — the scoped reference query against the old collect-then-scope path,
  for every function declared in a real bo3 and cod4 index (28,808 declarations): identical answers.
- `IndexedQueryCorpusTests` — the same check for every query that moved from a walk to an index:
  header dependents, rename plans, directive folder listings, class lookups, visible classes and
  header references, each asked every question of its kind a real bo3 and cod4 index allows, against
  the old walk kept in the test.
- `ClientArityHarvestTests` — what a game's SERVER library gets wrong against its CLIENT scripts, the
  input for building a CSC library from the GSC one. `StockScriptListTests` generates the per-game
  stock lists behind the raw-folder save warning.
- `HandlerCostTests` — what one CodeLens, inlay-hint and format request costs on the densest
  files. `InlayHintCorpusTests` — what the hints SAY over 250 files spread through bo3 and cod4:
  totals per family reported, and three things asserted — that neither dialect produces zero of
  either family (the merge dialects lost their parameter names once and nothing counted), that a
  40-line window returns exactly the whole-file hints inside it (the scroll-pruning invariant), and
  that no label repeats the word it sits on, read from the source TEXT so the check is not a
  restatement of the handler's own rule. It found the `DELETE_TRIGGER: delete_trigger` macro case on
  its first run. `MemoryProbeTests` (`Perf`) — what a full index RETAINS, watched for fifteen seconds, built
  to be carried onto older commits for bisecting. `PerfReport` / `SweepReport` write the HTML pages.

**Samples.** `SampleScriptTests` — the hand-written worked example per game per language world in
`server/samples`, run through the whole diagnostic pipeline and checked against the `// expect`
comments in the scripts themselves. Two halves: that every declared diagnostic is produced, and that
NOTHING else is — the showcase files declare none, so a finding there is a false positive caught on
code written to be correct. `EveryLanguageWorldTheGameHasIsSampled` reads `ScriptExtensions` off the
profile, so a game that gains `.csc` fails until its showcase exists. Joins `GameProfileCollection`
like the corpus classes; needs no game install, since each sample folder IS a raw root.
`SampleExpectations` parses the comments, `SampleWorkspace` indexes and analyses one game.
See `server/samples/FOLDER.md`.

This suite is why `GSCode.Server.Tests` no longer runs its collections in parallel, and why
`SampleWorkspace` puts `GameProfile.Active` back when it is done. It is the first class OUTSIDE
`Corpus/` to move the active dialect, and three hundred classes here read that global without
saying so: leaving it on CoD4 failed 132 of them in the formatter and the handlers, for reasons that
looked like the thing under test. Serializing alone still left 121, since a class running after a
sample run reads the leftover as happily as one running beside it — both halves are needed while a
global decides what the parser and the lints do. The serialization costs about a second on 313
tests, and nothing within a test: the corpus sweeps still parallelise their own file walk.

**Formatting.** `GscFormatterTests` the formatter at large · `FormatMinimalEditsTests` minimal edits ·
`StaleFormatEditTests` edits against a changed buffer · `UnbracedBodyFormattingTests`,
`UnbracedBodyShapeTests` braceless bodies · `ElseIfChainTests` · `OperatorSpacingTests`,
`BracketSpacingTests` · `ColumnAlignerTests`, `AssignmentAlignerTests` · `DirectiveSorterTests` ·
`FormatOptionsTests` the settings layer · `FormatPragmaTests` `#pragma disable format` ·
`GuidelineExampleTests` the examples in `FORMATTING.md`.

**Handlers.** `CodeActionHandlerTests` quick fixes · `CodeActionLintReuseTests` one request runs
the lint pass once, asserted by array identity rather than by counting calls on a sealed type · `DependentDiagnosticsTests` debounced
cross-file refreshes for other open documents, plus `ClosedDependentsOf` (the `full`-mode half): a
closed file referencing the origin's function is named, the origin itself and an open caller are
not · `ClosedDependentsDialectTests` the same on both dialect families, found under the key the
references were indexed with rather than one rebuilt from the declaration · `WorkspaceLintSweepTests` the `workspaceIndexingMode: full` sweep — a closed record's stored
diagnostics gain the cross-file lints, an open document is skipped and left untouched, and
`RelintClosedFilesAsync` upgrades only the file it was given · `CodeLensArgumentTests` the lens
command payload, which must be primitives so no serializer can case-mangle it ·
`CompletionResolveDataTests` ·
`DiagnosticsScopeTests` `gscode.diagnostics.scope` · `BuiltinAtTests` the builtin-under-cursor
request · `OnTypeBlockScopeTests` · `UntitledDocumentTests` documents with no path ·
`WorkspaceFoldersHandlerTests` · `IndexProgressNotifierTests`, `ServerStatusNotifierTests` ·
`DocumentSymbolNamelessTests` a half-typed declaration, which used to fail the WHOLE outline
request · `RenameScopeTests` what may be renamed, drawn on ownership rather than kind ·
`RenameNameValidationTests` what a rename may rename TO · `MacroRenameAcrossLanguagesTests` a macro
rename reaching both language worlds · `BuiltinReferenceTests` a builtin found across every
namespace, class and `sys::` form that calls it, and a script function sharing an engine name keeping
its own · `MacroReferenceScopeTests`, `DefinitionHandlerMacroScopeTests`
two same-named macros in sibling `.gsc`/`.csc` files staying apart · `DocumentHighlightSameFileTests`
highlight on the shared reference query, asked for one file · `CallHierarchyGroupingTests` one incoming entry per calling
FUNCTION · `CallHierarchyDialectTests` a caller expanding to its own callers on both dialect families · `ResolveForQueryTests` hierarchies for files not open · `NamespaceImportFixTests` the
add-`#using` fix driven through the handler · `WorkspaceSymbolShadowingTests` · `KeywordHoverTests`,
`BuiltinMacroHoverTests` (`__FUNCTION__`/`__FILE__` where written), `HoverInferenceReuseTests`
one assignment walk per version · `CompletionLabelDetailsTests`,
`CompletionSortTextTests` · `InlayHintMacroTests`, `InlayHintTypeCacheTests` one flow pass per version ·
`InlayHintParameterTests` the on-by-default parameter-name family, as the user sees it ·
`InlayHintMergeDialectTests` the same hints on a cross-file call under a merge dialect ·
`FieldNavigationTests` navigation from a field, answered from its writes (definition) and from what
a write binds (type definition, implementation, the hierarchies) · `ClassMemberNavigationTests` a
class `var` read as a bare name inside the class body · `ImplementationAndTypeDefinitionTests`
overriding subclasses and what a local holds, both declining rather than falling back to the
declaration · `HoverDefinitionLinkTests` the definition link under a hover's signature, and none
for a builtin · `AutoImportCompletionTests` the directive edit an unimported candidate carries ·
`GenerateScriptDocTests` a generated ScriptDoc block reading back as documentation in both dialect
forms.

**Analysis ordering and publishing.** `AnalysisGateTests`, `SingleFlightAnalysisTests` one analysis
in flight per document and none for a document nothing holds · `DiagnosticsPublishOrderTests` the
newest analysis wins whatever order they finish in · `DependentRefreshStampTests` a refresh publishes
the version it ANALYSED · `DiagnosticsUriTests` one file, one URI spelling ·
`WorkspaceDiagnosticsRefreshTests` a closed file resent only when its diagnostics changed ·
`ResolveFreshCancellationTests`, `SignatureHelpCancellationTests` read paths dropping a cancelled
request · `StartupRacePreAnalysisTests` folding and selection answered before any analysis
published.

**Configuration and mapping.** `SettingsReachTheServerTests` that a setting actually arrives ·
`SupportedGamesHandlerTests` the game picker's roster — exactly the supported profiles, the tick on
what the server SELECTED rather than what was asked for, and the two cross-file checks that close
the gap the original bug lived in: that the roster and the `gscode.game` enum are the same list,
and that `gscode.selectGame` is both declared in the manifest and registered in `extension.ts` ·
`EffectiveSummaryTests` · `DiagnosticMappingTests` our diagnostics to LSP · `SettingsSnapshotTests` a
settings push applied all at once · `InlayFamiliesTests` the change detector behind the inlay refresh ·
`StartupLogControllerTests`, `StartupLogLevelTests` the startup log channel · `TransportSelectionTests`
a command line naming a transport badly is refused, not turned into stdio · `PaddingOptionsTests` the
three padding knobs · `SourceEncodingTests` the repository gate on charset and line endings, including
the carriage returns git's own policy does not catch. `AssemblyInfo` runs this assembly's collections
one at a time — see the samples note above.
