# Old-test porting ledger

Every test class from the v1 suite (36 classes), tracked as its scenarios get
re-expressed against the v2 API (old test code is never copied) or consciously dropped.
Status: ☐ pending · ◐ partially ported · ✔ ported · ✘ dropped (with reason).

| Old test class | Status | Where / why |
|---|---|---|
| LexerTests | ✔ P1 | Scenarios re-expressed in `Lexing/LexerBasicsTests` (identifiers, numerics, keywords, operators, delimiters, member access, comments) + `LexerDirectiveTests` |
| TokenRangeTests | ✔ P1 | The half-open (end-exclusive) contract is `SourceTextTests.Range_Contains_IsHalfOpen` |
| StringSizeAndBreakTests | ✔ P14 | `.size` types as int in FlowTyper (`SizeProperty_IsInt`); unterminated-string lexing in `LexerStringTests`; the readonly-`.size`-write diagnostic is `Analysis/ReadOnlyWriteLintTests` |
| ParserTests | ✔ P3 | Scenarios re-expressed as S-expression goldens in `Syntax/DeclarationTests`, `StatementTests`, `ExpressionTests` |
| BrokenFunctionTests | ✔ P3 | Error-recovery scenarios in `Syntax/RecoveryTests` (garbage between functions, broken statements, missing braces/semicolons, class-member garbage) |
| DevBlockTests | ✔ P3 | Top-level + statement-level dev blocks in `DeclarationTests`/`StatementTests`; lexer-level delimiters in `LexerTriviaTests` |
| SwitchSemanticTokenTests | ✔ P8 | Semantic classification covered by `Extraction/SemanticTokenBuilderTests` (keywords/numbers/strings/functions/property/macro/comments) |
| CfaTests | ✔ P14 | Branch-join convergence wired into the walk; `Typing/TypeFlowConvergenceTests` covers if/else, loops, switch and nesting |
| TypeFlowConvergenceTests | ✔ P14 | Baseline in `Typing/FlowTyperTests`; branch-convergence cases now in `Typing/TypeFlowConvergenceTests` (agreeing branches, disagreeing → Unknown, int+float widening, assign-in-one-arm, loops, do-while, switch with/without default, nesting) |
| ScrDataApiTypeTests | ✔ P14 | Engine object-field type seeds wired into FlowTyper (`owner.field` types when every declaring entity kind agrees); the ReadOnly-field write diagnostic is `Analysis/ReadOnlyWriteLintTests` |
| DiagnosticsTests | ✔ P14 | v2 spreads diagnostics across the layer that owns each rule rather than one class: lexer/preprocessor codes in `Lexing`/`Preprocessing` tests, spec rules in `Extraction` tests (incl. `DuplicateFunctionTests`), and the workspace lints in `Analysis/*LintTests` |
| NamespaceDiagnosticsTests | ✔ P11 | Namespace-usage lint re-expressed in `Analysis/NamespaceUsageLintTests` (warns on unimported qualified-call namespace; suppressed when a #using is unresolved); merged into open-doc diagnostics by TextSyncHandler |
| PreferBooleanLiteralTests | ✔ P14 | `Analysis/PreferBooleanLiteralLintTests`, scoped to declared-`bool` parameters only — the v1 rule's own regression scenario (int/number params must not be flagged) |
| GlobalObjectOwnersTests | ✔ P14 | Field completion aggregates assignments across every visible record, scoped to the owner by default (`CompletionEngineTests`: owner scoping, `all` widening, cross-file aggregation, unknown-owner fallback) |
| SymbolTableTests | ✔ P14 | Successor concept: `ScriptRecord` + `LanguageStore`. Extraction surface covered by `Extraction/ExtractionTests`, storage and lookup by `Database/ScriptDatabaseTests` |
| WorkspaceCacheManagerTests | ✔ P6 | `Cache/SqliteCacheTests` (round-trip, identity-mismatch wipe, dirty-skip, delete) |
| CacheRestoreTests | ✔ P6 | `Cache/SqliteCacheTests.ColdRestoreTests` (unchanged files restore from cache) |
| GshInvalidationTests | ✔ P6 | `ColdRestoreTests.ChangedGshBetweenStarts_ReindexesDependents` (phase-two propagation) + P5 `WatchedFileUpdaterTests` |
| MemoryOptimizationTests | ✔ P14 | Superseded by measurement rather than assertions: `server/PERF.md` records a cold and warm index of 1,105 files with an identical live set (116.6 vs 112.7 MB), which is the records-only retention property this class asserted, proven on real data |
| MultiNamespaceMergeTests | ✔ P5 | `ScriptDatabaseTests.NamespaceMerging_UnionsAcrossContributingFiles` |
| GlobalSymbolRegistryTests | ✔ P5 | Successor concept: LanguageStore + ReferenceIndex; covered by `ScriptDatabaseTests` (store routing, isolation, shadowing, language guard) |
| ScriptManagerNamespaceScopingTests | ✔ P14 | Namespace scoping covered by `ScriptDatabaseTests` (merging across contributing files, private visibility scoped to the namespace) and `CompletionEngineTests` (`ns::` filtering, private functions offered only within the namespace) |
| ScriptDependenciesReadyTests | ✘ P14 | Dropped: the failure mode no longer exists. v1 needed a readiness gate because records mutated in place; v2 records are immutable and swapped atomically, so a query always reads a coherent snapshot and there is nothing to wait for |
| ScriptInsertPathsTests | ✔ P2 | Insert path collection/validation/edge scenarios in `Preprocessing/InsertTests` |
| CompletionAfterInsertTests | ✔ P8 | Context-aware completion (ns::, member, precache, statement/top-level scope) in `Completion/CompletionEngineTests`; macros from inserts resolve via the preprocessor |
| ScriptReferencesSelectionEndTests | ✔ P14 | The underlying contract is half-open ranges, pinned by `SourceTextTests.Range_Contains_IsHalfOpen`; boundary behaviour at a selection end follows from it, and cross-file references are covered by the P7 navigation smoke |
| ScriptHoverQualifiedFunctionTests | ✔ P7 | Qualified-function hover via `Api/ApiLoaderTests` (renderer) + the nav smoke; deeper cases as more hover tests land |
| ScriptDefinitionQualifiedIdentifierTests | ✔ P7 | Cross-file qualified definition verified by the P7 navigation smoke |
| MacroCallSiteModeTests | ☐ P14 | GENUINELY OPEN. Macro hover renders the `#define` signature and doc comment but no expansion preview, and `MacroRecord` does not carry the body (bodies stay parser-side). Needs the body plumbed into the record before the preview can exist |
| MacroGoToDefinitionWrongTargetTests | ✔ P2 | The canonical provenance regression: `InsertTests.Insert_SplicesTokens_WithGshProvenance` + `Insert_MacroFromGsh_DefinitionSitePointsIntoGsh` assert gsh-local ranges survive splicing (P7 wires it to go-to-def) |
| DocumentHighlightCasingTests | ✔ P7 | Same-file highlight verified by the P7 navigation smoke; keys are lowercase-canonical so casing is inherently handled |
| StockScriptsTests | ✔ P14 | `Api/StockScripts.cs` loads the list; `Resolution/RawWriteGuardTests` covers loading, slash/case-insensitive lookup, and every rawFileWarningMode branch |
| UsingDirectiveHelperTests | ✔ P2 | Path resolution scenarios re-expressed in `Resolution/PathResolverTests` (overlay, probe order, slash styles, illegal paths) |
| ReferenceIndexReconciliationTests | ✔ P5 | Index diff-on-swap covered by `ScriptDatabaseTests` (reference queries after re-index) + `WatchedFileUpdaterTests` |
| ReferencesHandlerTests | ✔ P7 | Cross-file references verified by the P7 navigation smoke; code-lens counts by the P9 smoke |
| LexerTests (v1 TestHelper trivia filtering) | ✔ P1 | Superseded by trivia-as-tokens + `LexTestHelper.SignificantTokens` |

## Unresolved rows

Every row is now resolved except one, tracked in `server/FOLLOWUPS.md`:

- **MacroCallSiteModeTests** (☐) — macro expansion preview; needs the macro body carried on
  `MacroRecord` first.

Resolutions favour pointing at where a scenario actually lives over inventing a same-named
class, and one row is a conscious drop: `ScriptDependenciesReadyTests` tested a readiness gate
that v2's immutable-record design removes entirely.

## P11 code-action mining (completed in P14 wave 6)

Landed in P11: remove-duplicate-`#using` and auto-add-`#using` (`CodeActionHandlerTests`), plus
the whole/range/on-type formatter (`GscFormatterTests`) and the namespace-usage lint
(`NamespaceUsageLintTests`).

The v1 `CodeActionHandler.cs` (1,081 lines) was then mined. Its actions were re-expressed, never
copied. The decisive filter: **a code action is only reachable if something reports the problem**,
so most v1 fixes would have required porting a diagnostic first.

**Ported** — each one's trigger already exists in v2:

| v1 action | v2 trigger | Notes |
|---|---|---|
| Remove unused `#using` | `UnusedUsing` (5001, wave 1) | Deletes the whole line, newline included |
| Remove ALL unused `#using` | same, 2+ reported | One click for the common cleanup |
| Replace `0`/`1` with `false`/`true` | `PreferBooleanLiteral` (5002, wave 3) | Replacement read from the SOURCE, not parsed out of the message, so it cannot drift if wording changes — and a stale range over other text yields no fix |
| Move misplaced `#using` above the first declaration | `UsingAfterDeclaration` (3009) | Delete + re-insert as one edit; the insertion point ignores directives at or below the offending line, or it would target a point below itself |

**Dropped, with reasons:**

- *Unused variable, unused parameter, duplicate macro definition, duplicate modifier,
  unreachable code, duplicate case label, multiple default labels, unreachable case,
  consumed threaded call result, store-function-as-pointer* — v2 emits none of these
  diagnostics, and switch-exhaustiveness analysis was consciously dropped in the plan. Porting
  the fix without the diagnostic produces dead code.
- *Generate function stub / create missing file* — speculative code generation from a quick
  fix. Heavy, and both guess at intent (where does the file go, what signature?).
- *Add matching `/* endregion */` and append `#endif`* — both append at a guessed position.
  For `#endif` especially, guessing wrong silently changes which branch is active, which is
  worse than the unterminated-directive diagnostic the user already sees.
- *Square-bracket initialiser → `array()`* — no corresponding v2 diagnostic.
- *Add `#using` for an unknown namespace / namespaced function* — already shipped in P11.
