# Old-test porting ledger

Every test class from the v1 suite (36 classes), tracked as its scenarios get
re-expressed against the v2 API (old test code is never copied) or consciously dropped.
Status: ☐ pending · ◐ partially ported · ✔ ported · ✘ dropped (with reason).

| Old test class | Status | Where / why |
|---|---|---|
| LexerTests | ✔ P1 | Scenarios re-expressed in `Lexing/LexerBasicsTests` (identifiers, numerics, keywords, operators, delimiters, member access, comments) + `LexerDirectiveTests` |
| TokenRangeTests | ✔ P1 | The half-open (end-exclusive) contract is `SourceTextTests.Range_Contains_IsHalfOpen` |
| StringSizeAndBreakTests | ◐ P10 | `.size` types as int in FlowTyper (`SizeProperty_IsInt`); the unterminated-string lexing half is covered by `LexerStringTests`. The readonly-`.size`-write diagnostic is still pending |
| ParserTests | ✔ P3 | Scenarios re-expressed as S-expression goldens in `Syntax/DeclarationTests`, `StatementTests`, `ExpressionTests` |
| BrokenFunctionTests | ✔ P3 | Error-recovery scenarios in `Syntax/RecoveryTests` (garbage between functions, broken statements, missing braces/semicolons, class-member garbage) |
| DevBlockTests | ✔ P3 | Top-level + statement-level dev blocks in `DeclarationTests`/`StatementTests`; lexer-level delimiters in `LexerTriviaTests` |
| SwitchSemanticTokenTests | ✔ P8 | Semantic classification covered by `Extraction/SemanticTokenBuilderTests` (keywords/numbers/strings/functions/property/macro/comments) |
| CfaTests | ☐ P10 | FlowTyper's per-function walk visits every branch now; branch-join convergence (the `ScrType.Join` lattice) not yet wired into the walk |
| TypeFlowConvergenceTests | ◐ P10 | Assignment-inference accuracy baseline established in `Typing/FlowTyperTests` (literals, arithmetic widening/concat, globals, builtin returns, first-assignment-wins); branch-convergence cases still pending |
| ScrDataApiTypeTests | ◐ P10 | Engine object-field type seeds wired into FlowTyper (`owner.field` types when every declaring entity kind agrees); the ReadOnly-field write diagnostic is still pending |
| DiagnosticsTests | ☐ P4/P5 | |
| NamespaceDiagnosticsTests | ✔ P11 | Namespace-usage lint re-expressed in `Analysis/NamespaceUsageLintTests` (warns on unimported qualified-call namespace; suppressed when a #using is unresolved); merged into open-doc diagnostics by TextSyncHandler |
| PreferBooleanLiteralTests | ☐ P4 | Carried lint rule |
| GlobalObjectOwnersTests | ☐ P8 | Field aggregation |
| SymbolTableTests | ☐ P4/P5 | |
| WorkspaceCacheManagerTests | ✔ P6 | `Cache/SqliteCacheTests` (round-trip, identity-mismatch wipe, dirty-skip, delete) |
| CacheRestoreTests | ✔ P6 | `Cache/SqliteCacheTests.ColdRestoreTests` (unchanged files restore from cache) |
| GshInvalidationTests | ✔ P6 | `ColdRestoreTests.ChangedGshBetweenStarts_ReindexesDependents` (phase-two propagation) + P5 `WatchedFileUpdaterTests` |
| MemoryOptimizationTests | ☐ P5/P6 | Re-express as record-retention assertions if still meaningful |
| MultiNamespaceMergeTests | ✔ P5 | `ScriptDatabaseTests.NamespaceMerging_UnionsAcrossContributingFiles` |
| GlobalSymbolRegistryTests | ✔ P5 | Successor concept: LanguageStore + ReferenceIndex; covered by `ScriptDatabaseTests` (store routing, isolation, shadowing, language guard) |
| ScriptManagerNamespaceScopingTests | ☐ P5 | |
| ScriptDependenciesReadyTests | ☐ P5 | |
| ScriptInsertPathsTests | ✔ P2 | Insert path collection/validation/edge scenarios in `Preprocessing/InsertTests` |
| CompletionAfterInsertTests | ✔ P8 | Context-aware completion (ns::, member, precache, statement/top-level scope) in `Completion/CompletionEngineTests`; macros from inserts resolve via the preprocessor |
| ScriptReferencesSelectionEndTests | ☐ P7 | |
| ScriptHoverQualifiedFunctionTests | ✔ P7 | Qualified-function hover via `Api/ApiLoaderTests` (renderer) + the nav smoke; deeper cases as more hover tests land |
| ScriptDefinitionQualifiedIdentifierTests | ✔ P7 | Cross-file qualified definition verified by the P7 navigation smoke |
| MacroCallSiteModeTests | ☐ P7 | Macro expansion preview scenarios |
| MacroGoToDefinitionWrongTargetTests | ✔ P2 | The canonical provenance regression: `InsertTests.Insert_SplicesTokens_WithGshProvenance` + `Insert_MacroFromGsh_DefinitionSitePointsIntoGsh` assert gsh-local ranges survive splicing (P7 wires it to go-to-def) |
| DocumentHighlightCasingTests | ✔ P7 | Same-file highlight verified by the P7 navigation smoke; keys are lowercase-canonical so casing is inherently handled |
| StockScriptsTests | ☐ P5 | Stock list powers rawFileWarningMode=stock |
| UsingDirectiveHelperTests | ✔ P2 | Path resolution scenarios re-expressed in `Resolution/PathResolverTests` (overlay, probe order, slash styles, illegal paths) |
| ReferenceIndexReconciliationTests | ✔ P5 | Index diff-on-swap covered by `ScriptDatabaseTests` (reference queries after re-index) + `WatchedFileUpdaterTests` |
| ReferencesHandlerTests | ✔ P7 | Cross-file references verified by the P7 navigation smoke; code-lens counts by the P9 smoke |
| LexerTests (v1 TestHelper trivia filtering) | ✔ P1 | Superseded by trivia-as-tokens + `LexTestHelper.SignificantTokens` |

## Unresolved rows

The ☐ rows above are tracked as P14 item 17 in `server/FOLLOWUPS.md`; each needs a port or
a conscious drop with a reason. The remaining halves of the ◐ rows are P14 items 5, 4,
and 9 (`.size` write diagnostic, ReadOnly-field write diagnostic, branch-join convergence).

## P11 code-action mining (deferred to P13)

Landed: remove-duplicate-#using and auto-add-#using (`CodeActionHandlerTests`), plus the
whole/range/on-type formatter (`GscFormatterTests`) and the namespace-usage lint
(`NamespaceUsageLintTests`). Deferred to P13: mine the v1 `CodeActionHandler.cs` (1,081 lines) via
git history for any OTHER quick fixes worth porting and record keep/drop decisions here.
