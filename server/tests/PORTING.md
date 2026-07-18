# Old-test porting ledger

Every test class from the v1 suite (36 classes), tracked as its scenarios get
re-expressed against the v2 API (old test code is never copied) or consciously dropped.
Status: ☐ pending · ✔ ported · ✘ dropped (with reason).

| Old test class | Status | Where / why |
|---|---|---|
| LexerTests | ✔ P1 | Scenarios re-expressed in `Lexing/LexerBasicsTests` (identifiers, numerics, keywords, operators, delimiters, member access, comments) + `LexerDirectiveTests` |
| TokenRangeTests | ✔ P1 | The half-open (end-exclusive) contract is `SourceTextTests.Range_Contains_IsHalfOpen` |
| StringSizeAndBreakTests | ☐ P10 | `.size` on strings/istrings is FlowTyper behavior (readonly int, write → diagnostic); the unterminated-string lexing half is covered now by `LexerStringTests` |
| ParserTests | ✔ P3 | Scenarios re-expressed as S-expression goldens in `Syntax/DeclarationTests`, `StatementTests`, `ExpressionTests` |
| BrokenFunctionTests | ✔ P3 | Error-recovery scenarios in `Syntax/RecoveryTests` (garbage between functions, broken statements, missing braces/semicolons, class-member garbage) |
| DevBlockTests | ✔ P3 | Top-level + statement-level dev blocks in `DeclarationTests`/`StatementTests`; lexer-level delimiters in `LexerTriviaTests` |
| SwitchSemanticTokenTests | ☐ P8 | |
| CfaTests | ☐ P10 | Control-flow scenarios inform FlowTyper's per-function walk |
| TypeFlowConvergenceTests | ☐ P10 | The accuracy baseline |
| ScrDataApiTypeTests | ☐ P10 | |
| DiagnosticsTests | ☐ P4/P5 | |
| NamespaceDiagnosticsTests | ☐ P5 | Namespace-usage lint |
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
| CompletionAfterInsertTests | ☐ P8 | |
| ScriptReferencesSelectionEndTests | ☐ P7 | |
| ScriptHoverQualifiedFunctionTests | ✔ P7 | Qualified-function hover via `Api/ApiLoaderTests` (renderer) + the nav smoke; deeper cases as more hover tests land |
| ScriptDefinitionQualifiedIdentifierTests | ✔ P7 | Cross-file qualified definition verified by the P7 navigation smoke |
| MacroCallSiteModeTests | ☐ P7 | Macro expansion preview scenarios |
| MacroGoToDefinitionWrongTargetTests | ✔ P2 | The canonical provenance regression: `InsertTests.Insert_SplicesTokens_WithGshProvenance` + `Insert_MacroFromGsh_DefinitionSitePointsIntoGsh` assert gsh-local ranges survive splicing (P7 wires it to go-to-def) |
| DocumentHighlightCasingTests | ✔ P7 | Same-file highlight verified by the P7 navigation smoke; keys are lowercase-canonical so casing is inherently handled |
| StockScriptsTests | ☐ P5 | Stock list powers rawFileWarningMode=stock |
| UsingDirectiveHelperTests | ✔ P2 | Path resolution scenarios re-expressed in `Resolution/PathResolverTests` (overlay, probe order, slash styles, illegal paths) |
| ReferenceIndexReconciliationTests | ☐ P5 | |
| ReferencesHandlerTests | ☐ P7 | |
| LexerTests (v1 TestHelper trivia filtering) | ✔ P1 | Superseded by trivia-as-tokens + `LexTestHelper.SignificantTokens` |
