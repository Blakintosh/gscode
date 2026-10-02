# Feature catalog — every user-visible feature, and where it lives

One row per thing a user can see or trigger. Each row names the code that serves it and the tests
that pin it, so a change to a feature starts from the right file and ends at the right test.

- **Handler** is in `server/src/GSCode.Server/Handlers/` unless a path says otherwise.
- **Engine** is the code that computes the answer. Handlers stay thin; the engine lives in
  `GSCode.Workspace` or `GSCode.Parser`.
- **Tests** name a class; `server/tests/FOLDER.md` says what each one covers. Search that file for
  the construct you are changing — there are usually more classes than fit in a row.
- How each feature behaves in detail, and why: the handler's section in
  `server/src/GSCode.Server/FOLDER.md`.

---

## Editor features (LSP)

| Feature | LSP method | Handler | Engine | Setting | Tests |
|---|---|---|---|---|---|
| Diagnostics while typing | `textDocument/publishDiagnostics` (push) | `TextSyncHandler`, `DocumentLinter`, `DiagnosticsPublisher` | `ScriptAnalysis.Analyze` + `WorkspaceLints.Analyze` | — | `StaleAnalysisTests`, `DiagnosticsPublishOrderTests`, every `*LintTests` |
| Diagnostics for closed files | same, for files not open | `WorkspaceDiagnosticsPublisher`, `WorkspaceLintSweep` | stored `ScriptRecord.Diagnostics` | `gscode.diagnostics.scope`, `gscode.workspaceIndexingMode` | `DiagnosticsScopeTests`, `WorkspaceLintSweepTests`, `WorkspaceDiagnosticsRefreshTests` |
| Re-lint other files after an edit | (republish) | `DependentDiagnosticsRefresher` | `ExportSignature`, `LanguageStore.FilesReferencing` | — | `DependentDiagnosticsTests`, `ClosedDependentsDialectTests` |
| Outline | `textDocument/documentSymbol` | `DocumentSymbolHandler` | extraction (`ExtractionResult`) | `gscode.outline.showAssignments` | `DocumentSymbolNamelessTests` |
| Folding | `textDocument/foldingRange` | `FoldingRangeHandler` | `FoldingRegions.Compute` | — | `StartupRacePreAnalysisTests` |
| Expand selection | `textDocument/selectionRange` | `SelectionRangeHandler` | `AstSearch.ChainAt` | — | `StartupRacePreAnalysisTests` |
| Hover | `textDocument/hover` | `HoverHandler` | `SymbolAtPosition`, `DatabaseQueries`, `MarkdownDocRenderer`, `KeywordDocs`, `FlowTyper` | — | `KeywordHoverTests`, `HoverDefinitionLinkTests`, `SysQualifiedHoverTests`, `BuiltinMacroHoverTests` |
| Go to definition | `textDocument/definition` | `DefinitionHandler` | `DatabaseQueries.PreferIncludeScope`, `LocalDefinition` | — | `DefinitionHandlerMacroScopeTests`, `FieldNavigationTests`, `LocalDefinitionTests` |
| Go to implementation | `textDocument/implementation` | `ImplementationHandler` | `ClassGraph`, `MethodResolution`, `FieldTargets` | — | `ImplementationAndTypeDefinitionTests` |
| Go to type definition | `textDocument/typeDefinition` | `TypeDefinitionHandler` | `FlowTyper.TryGetValueAt`, `FieldTargets` | — | `ImplementationAndTypeDefinitionTests` |
| Find references | `textDocument/references` | `ReferencesHandler` | `NavigationSupport.FindAllReferences`, `LocalReferences` | — | `ReferenceScopingTests`, `BuiltinReferenceTests`, `LocalReferencesTests` |
| Highlight occurrences | `textDocument/documentHighlight` | `DocumentHighlightHandler` | `NavigationSupport.FindReferencesInFile` | — | `DocumentHighlightSameFileTests` |
| Clickable import paths | `textDocument/documentLink` | `DocumentLinkHandler` | `NavigationSupport.ResolveDirectivePath` | — | — |
| Semantic highlighting | `textDocument/semanticTokens/*` | `SemanticTokensHandler` | `SemanticTokenBuilder`, `LocalReferences.SemanticTokens` | — | `SemanticTokenBuilderTests`, `LocalSemanticTokensTests` |
| Completion | `textDocument/completion` (+ resolve) | `CompletionHandler` | `CompletionEngine`, `GscKeywords`, `GscSnippets` | `gscode.completion.*` | `CompletionEngineTests`, `RealisticKeystrokeTests`, `AutoImportCompletionTests`, `NarrowedCompletionTests` |
| Signature help | `textDocument/signatureHelp` | `SignatureHelpHandler` | `SignatureEngine`, `CallResolution` | — | `SignatureEngineTests`, `SignatureHelpCancellationTests` |
| Reference-count lens | `textDocument/codeLens` | `CodeLensHandler`, `CodeLensRefresh.cs` | `NavigationSupport.FindAllReferences` | `gscode.codeLens.enabled` | `CodeLensArgumentTests` |
| Rename symbol | `textDocument/rename`, `prepareRename` | `RenameHandler`, `PrepareRenameHandler` | `FindAllReferences`, `LocalReferences`, `GscIdentifier` | — | `RenameScopeTests`, `RenameNameValidationTests`, `MacroRenameAcrossLanguagesTests` |
| Call hierarchy | `callHierarchy/*` | `CallHierarchyHandler` | reference index, `EnclosingFunction`, `MethodResolution` | — | `CallHierarchyGroupingTests`, `CallHierarchyDialectTests`, `CallHierarchyMethodTests` |
| Type hierarchy | `typeHierarchy/*` | `TypeHierarchyHandler` | `ClassGraph` | — | `ResolveForQueryTests` |
| Inlay hints | `textDocument/inlayHint` | `InlayHintHandler` | `FlowTyper.InferValuesShared`, `CallResolution`, `MacroExpansionPreview` | `gscode.inlayHints.*` | `InlayHintParameterTests`, `InlayHintMacroTests`, `InlayHintMergeDialectTests`, `InlayHintTypeCacheTests` |
| Format document | `textDocument/formatting` | `DocumentFormattingHandler` | `Formatting/GscFormatter` | `gscode.format.*` | `GscFormatterTests`, `FormatMinimalEditsTests` |
| Format selection | `textDocument/rangeFormatting` | `DocumentRangeFormattingHandler` | same | same | `LocalFormatEditTests` |
| Format on type (`}` `;`) | `textDocument/onTypeFormatting` | `DocumentOnTypeFormattingHandler` | same + `FormatScope` | `editor.formatOnType` | `OnTypeBlockScopeTests` |
| Quick fixes | `textDocument/codeAction` | `CodeActionHandler`, `ImportEdits` | lint results + `DatabaseQueries` | — | `CodeActionHandlerTests`, `NamespaceImportFixTests`, `CodeActionLintReuseTests` |
| Generate ScriptDoc | `gscode/generateScriptDoc` (command, not a code action) | `GenerateScriptDocHandler` | `Core/Docs/ScriptDocTemplate` | — | `GenerateScriptDocTests` |
| Workspace symbol search | `workspace/symbol` | `WorkspaceSymbolHandler` | both language stores | — | `WorkspaceSymbolShadowingTests` |
| Watched file changes | `workspace/didChangeWatchedFiles` | `WatchedFilesHandler` | `WatchedFileUpdater` | — | `WatchedFileUpdaterTests`, `WatcherRaceTests`, `HeaderChangeReachTests` |
| Multi-root folders | `workspace/didChangeWorkspaceFolders` | `WorkspaceFoldersHandler` | `RootConfig`, `WorkspaceIndexer` | — | `WorkspaceFoldersHandlerTests` |
| Settings | `workspace/didChangeConfiguration` | `ConfigurationHandler` | `ServerSettings` | all | `SettingsReachTheServerTests`, `SettingsSnapshotTests`, `InlayFamiliesTests` |

Quick fixes currently offered by `CodeActionHandler`:

| Diagnostic | Fix |
|---|---|
| 5018 duplicate import | Remove the duplicate `#using` / `#include` line |
| 5000 namespace not imported | Add the `#using` for the file declaring the namespace |
| 5026 function not included | Add `#include` for a file declaring the function |
| 5013 / 5014 unresolved call | Create the function in this file; or add `#using` and qualify the call (namespace dialects) |
| 5001 / 5012 unused import | Remove it (also "remove all unused imports"). Organize Imports removes them and sorts the directive block |
| 3009 `#using` after a declaration | Move it up |
| 5002 prefer boolean literal | Replace `0`/`1` with `false`/`true` |

To confirm this list against the code: `grep -n "GscDiagnosticCode\." server/src/GSCode.Server/Handlers/CodeActionHandler.cs`.

---

## Custom protocol messages (`gscode/*`)

Everything the client and server say to each other outside standard LSP. The two sides are in
`client/src/extension.ts` (or `gamePicker.ts`) and the server file named.

| Message | Direction | Server side | What it is for |
|---|---|---|---|
| `gscode/serverReady` | server → client | `Startup/StartupIndex.cs` | The game the server actually selected, for the status bar |
| `gscode/indexingStarted` | server → client | `IndexProgressNotifier` | Total file count; starts the status-bar counter |
| `gscode/indexingProgress` | server → client | `IndexProgressNotifier` | Files done so far (throttled, never goes backwards) |
| `gscode/indexingComplete` | server → client | `IndexProgressNotifier` | Final count; stops the spinner |
| `gscode/indexingFailed` | server → client | `IndexProgressNotifier` | The startup pass threw; status bar shows a warning |
| `gscode/serverStatus` | server → client | `ServerStatusNotifier` | Working-set memory for the status-bar tooltip |
| `gscode/gameMismatch` | server → client | `TextSyncHandler` | Opened scripts look like a different game; carries the roster for the picker |
| `gscode/rawFolderWriteWarning` | server → client | `TextSyncHandler` | A save inside the protected raw folder (`RawWriteGuard`) |
| `gscode/supportedGames` | client → server request | `SupportedGamesHandler` | The game roster for the picker, and which game is running |
| `gscode/clearCache` | client → server request | `ClearCacheHandler` | Stop indexing, close and delete this workspace's cache |
| `gscode/planRename` | client → server request | `PlanRenameHandler` | Directive edits for a file being renamed |
| `gscode/builtinAt` | client → server request | `BuiltinAtHandler` | Is the symbol under the cursor an engine builtin (for `shift+f1`) |
| `gscode/generateScriptDoc` | client → server request | `GenerateScriptDocHandler` | The ScriptDoc block for the function at a position, and the line it goes above |

To regenerate this table: `grep -rhoE '"gscode/[A-Za-z]+"' client/src server/src | sort -u`.

---

## Commands

Declared in `client/package.json` (`contributes.commands`), registered in `client/src/extension.ts`.

| Command | Title | What it does |
|---|---|---|
| `gscode.showOutput` | GSCode: Show Server Output | Opens the "GSCode Server" channel (server stderr) |
| `gscode.restartServer` | GSCode: Restart Language Server | Restarts the language client. Does not apply a game change |
| `gscode.selectGame` | GSCode: Select Game | Picker over `gscode/supportedGames`, writes `gscode.game`, reloads |
| `gscode.clearCacheAndReindex` | GSCode: Clear Cache and Reindex | Modal confirm, `gscode/clearCache`, then window reload |
| `gscode.openApiLibrary` | GSCode: Open Documentation for Symbol | `shift+f1`. Opens gscode.net for the builtin under the cursor (`gscode/builtinAt`) or the library index |
| `gscode.organizeImports` | GSCode: Organize Imports | Asks for the `source.organizeImports` action at the cursor and applies it (`applyServerCodeAction`): removes unused imports and sorts the block with `DirectiveSorter`; says so when there is nothing to do |
| `gscode.generateScriptDoc` | GSCode: Generate ScriptDoc Block | Sends `gscode/generateScriptDoc` with the cursor and inserts the block it returns; says why when the cursor is in no function or the function is already documented |

Every command except `gscode.showReferences` is in the editor right-click menu under a **GSCode**
submenu (`contributes.submenus`, `menus["gscode.editorContext"]`), shown in gsc/csc/gsh editors.
Commands carry `"category": "GSCode"`, so the palette shows "GSCode: …" while the submenu shows the
bare title. `ClientCommandsTests` fails when a declared command is not registered, or a menu names an
undeclared command.
| `gscode.showReferences` | (internal) | Bridge the CodeLens command calls to open the references peek |

Client-only behaviour with no server counterpart: the rename fix-up listener
(`registerRenameDirectiveFixup`), semicolon de-duplication (`registerSemicolonDeduplication`), caret
restore after formatting (`caretRestore.ts`), the reload prompt (`reloadPrompt.ts`), the TextMate
grammar (`client/syntaxes/gsc.tmGrammar.json`), bracket and comment rules
(`client/language-configuration.json`) and the universal snippets (`client/snippets/common.json`;
dialect-specific snippets come from the server, `GscSnippets`).

---

## Settings

Every `gscode.*` key the client contributes, and the server file that reads it. `ServerSettings`
(`server/src/GSCode.Server/Configuration/ServerSettings.cs`) parses all of them and nothing else;
`client/src/settings.ts` is the client's copy of the shape. The user-facing description of each is in
`client/package.json` and `client/README.md`.

| Setting | Default | Read by | Takes effect |
|---|---|---|---|
| `gscode.game` | `bo3` | `Program.cs` (`--game`), `ServerServices.OnInitializeAsync` | Window reload |
| `gscode.raw.enabled` | `true` | `WorkspaceFoldersHandler.BuildConfig` → `RootConfig.Create` | Reload |
| `gscode.rawPath` | `""` | same | Reload |
| `gscode.modsPath` | `""` | same | Reload |
| `gscode.rawFileWarningMode` | `stock` | `TextSyncHandler` → `RawWriteGuard` | Live |
| `gscode.workspaceIndexingMode` | `partial` | `StartupIndexRunner`, `WorkspaceFoldersHandler` | Reload |
| `gscode.enableWorkspaceCache` | `true` | `StartupIndexRunner` | Reload |
| `gscode.diagnostics.scope` | `workspace` | `WorkspaceDiagnosticsPublisher`, `ConfigurationHandler` | Live (republishes) |
| `gscode.outline.showAssignments` | `true` | `DocumentSymbolHandler` | Live |
| `gscode.codeLens.enabled` | `false` | `CodeLensHandler` | Live |
| `gscode.inlayHints.parameterNames` | `true` | `InlayHintHandler` | Live (refresh sent) |
| `gscode.inlayHints.inferredTypes` | `true` | `InlayHintHandler` | Live (refresh sent) |
| `gscode.inlayHints.macroParameterNames` | `false` | `InlayHintHandler` | Live (refresh sent) |
| `gscode.completion.autoImport` | `true` | `CompletionHandler` | Live |
| `gscode.completion.literals` | `true` | `CompletionHandler` | Live |
| `gscode.completion.callPunctuation` | `parensAndSemicolon` | `CompletionHandler` | Live |
| `gscode.completion.parameterHints` | `true` | `CompletionHandler` | Live |
| `gscode.format.*` (11 keys) | see `package.json` | `Formatting/FormatOptions.cs` | Next format |
| `gscode.serverLogLevel` | `warning` | `ConfigurationHandler`, `StartupLogController` | Live |
| `gscode.trace.server` | `off` | vscode-languageclient itself | Live |

"Reload" means the value is read once, at initialize or startup, so a change applies on the next
window reload. `reloadPrompt.ts` offers that reload for `gscode.game`, `gscode.rawPath`,
`gscode.modsPath` and `gscode.raw.enabled` only; the indexing and cache settings wait for the user to
reload. "Live" means the value is read per request or re-applied by `ConfigurationHandler`.
The `workspaceIndexingMode` values: `off` analyses open files only and skips the lints that need the
index; `partial` indexes everything for navigation and lints open files; `full` also lints every
closed file (`WorkspaceLintSweep`).

To check this table: `grep -n "public .* { get" server/src/GSCode.Server/Configuration/ServerSettings.cs`
against `contributes.configuration` in `client/package.json`. `SettingsReachTheServerTests` fails when
a key declared in `package.json` is missing from the payload `client/src/settings.ts` sends.

---

## Diagnostics

Every diagnostic code, its severity, its owner and its fix: [diagnostics.md](diagnostics.md).
