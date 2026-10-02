# Lifecycles — what happens, in order

Each section follows one event from the editor to the result, naming the file and the symbol that
does each step. Line numbers are left out on purpose: they drift, a symbol name does not. When a
step needs more detail than one line, the per-file reference (`FOLDER.md` in each project) has it
under the same name.

Paths are relative to the repository root. `Server/` means `server/src/GSCode.Server/`,
`Workspace/` means `server/src/GSCode.Workspace/`, `Parser/` and `Core/` likewise.

---

## 1. Cold start: from opening a folder to an indexed workspace

**Client side** (`client/src/extension.ts`, `client/src/server.ts`)

1. VS Code activates the extension when the workspace contains a `.gsc`, `.csc` or `.gsh`
   (`activationEvents` in `client/package.json`).
2. `activate` creates the "GSCode" log channel, registers every command (`registerCommands`) and the
   reload prompt, then calls `createLanguageClient`.
3. `createLanguageClient` runs `isDotnetRuntimeAvailable` (`dotnet --list-runtimes`, looking for
   major version 10). Missing runtime: an error with a download link, and activation stops there.
4. It resolves the server folder (`resolveServerFolder`: the bundled `client/service/` in a packaged
   build, `client/.env` in a debug session), and spawns
   `dotnet GSCode.Server.dll --game <gscode.game>` over a **named pipe**. The game is a command-line
   argument, which is why changing it needs a window reload, not a server restart.
5. `initializationOptions.gscode` carries every `gscode.*` setting (`readSettings` in `settings.ts`).
6. After `start()`, the client wires the status bar (`registerIndexingStatusBar`), the rename
   fix-up (`registerRenameDirectiveFixup`) and semicolon de-duplication.

**Server side**

1. `Server/Program.cs` sets up Serilog to **stderr** (stdout is the stdio transport's wire), parses
   the command line into `TransportOptions`, and selects the game with `GameProfile.Select` **before
   anything else is built** — the bundled data singletons read `GameProfile.Active` when first
   resolved.
2. `TransportResolver.ResolveAsync` connects the pipe (or socket, or stdio). Bounded at 30 s, so a
   client that died between spawn and listen does not leave an orphan.
3. `Program.cs` builds the long-lived holders: `ServerSettings`, `ResolverHolder`, `CacheHolder`,
   `IndexingLifetime`, `ConnectionSettleGate`, `StartupIndexRunner`. Then
   `ServerServices.Configure` registers every singleton and every handler with OmniSharp.
4. **initialize** (`ServerServices.OnInitializeAsync`): applies the client settings, re-selects the
   game as a safety net, collects the workspace folders, builds `RootConfig` through
   `WorkspaceFoldersHandler.BuildConfig` and puts a new `PathResolver` in `ResolverHolder`. No game
   root found is a normal state ("workspace-only mode") and logs one line.
5. **initialized** (`OnInitializedAsync`): declares UTF-16 position encoding.
6. **started** (`StartupIndexRunner.RunAsync` in `Server/Startup/StartupIndex.cs`):
   - sends `gscode/serverReady` (through `ConnectionSettleGate`, because a notification sent inside
     the initialize window is dropped by the transport);
   - opens the SQLite cache when `gscode.enableWorkspaceCache` is on, whatever the indexing mode:
     `SqliteCache.ResolveDatabasePath` hashes the roots into
     `%APPDATA%/gscode/cache/<hash>.db`, `ServerBuildIdentity.Compute` fingerprints game + assemblies
     + bundled data, and `SqliteCache.Open` wipes the file when either version or identity differs.
     `LoadAll` hands the indexer the stored rows as `CachedEntry` blobs, not yet deserialized;
   - unless `gscode.workspaceIndexingMode` is `off`, starts the indexing task under
     `IndexingLifetime` (cancellable by shutdown and by clear-cache).
7. The indexing task (`WorkspaceIndexer.IndexAsync` in `Workspace/Indexing/WorkspaceIndexer.cs`):
   - enumerates targets (`PathResolver.EnumerateIndexTargets`: every script under raw, mods and the
     workspace folders, skipping `GameProfile.ToolOutputDirectories`);
   - runs a bounded `Parallel.ForEachAsync` over them. Per file: if the on-disk content hash matches
     the cached entry, deserialize the blob (`RecordSerializer.Deserialize`) and commit it;
     otherwise read, run `ScriptAnalysis.Analyze`, build the record, commit it and enqueue it to the
     cache (`SqliteCache.Enqueue` serializes on the calling thread; one background writer does the
     SQL);
   - phase two re-parses restored files that `#insert` a header which changed since last session;
   - a file the editor already has open is not overwritten (`ownedByEditor: documents.IsOpen`) —
     the buffer is the truth for an open file;
   - `IndexProgressNotifier` turns progress into `gscode/indexingStarted|Progress|Complete`,
     throttled to one per ~40 ms by `ProgressThrottle`.
8. When indexing finishes, `StartupIndexRunner` then:
   - in `full` mode, runs `WorkspaceLintSweep.RunFullSweepAsync` — the cross-file lints over every
     closed file;
   - publishes closed-file problems (`WorkspaceDiagnosticsPublisher.Refresh`) and re-lints open
     tabs (`DependentDiagnosticsRefresher.Schedule`), because tabs restored with the window were
     linted against a half-built index;
   - waits for the cache writer to go idle, runs one compacting GC (`Compact`), logs the memory
     report, and starts `ServerStatusNotifier` (the status-bar memory figure).
   - On failure it sends `gscode/indexingFailed`. Either way `StartupLogController.Settle` drops the
     log level to what the client asked for.

Where the time goes, and what each step measured at: `server/PERF.md`, sections "COLD INDEXING",
"WARM start" and "STARTUP".

---

## 2. Typing: from a keystroke to diagnostics

1. The client sends `textDocument/didChange` (incremental). `TextSyncHandler.Handle`
   (`Server/Handlers/TextSyncHandler.cs`) applies the edit with `DocumentStore.ApplyChange` and calls
   `ScheduleDebouncedAnalysis`.
2. `RunDebouncedAsync` waits `AnalysisTiming.DebounceMilliseconds` (250 ms). A newer edit cancels the
   pending one (`ReplacePendingAnalysis`), silently.
3. `SingleFlightAnalysis.Run` makes sure only one analysis per document runs at a time; a trigger
   that arrives while one is running queues a rerun.
4. `AnalyzeAndPublish`:
   1. `DocumentStore.AnalyzeSnapshot` runs the per-file pipeline (`Parser/ParseResult.cs`,
      `ScriptAnalysis.Analyze`): `Lexer.Lex` → `Preprocessor.Process` (macros, `#insert` through the
      file's `ResolverInsertProvider`, `#if`) → `Parser.Parse` → `SymbolExtractor.Extract`. The
      result and the document version it describes are stored together as one `AnalysisSnapshot`;
      an older version can never overwrite a newer one.
   2. `DocumentLinter.Analyze` calls `WorkspaceLints.Analyze`
      (`Workspace/Analysis/WorkspaceLints.cs`): the parse diagnostics plus every cross-file lint,
      then `ApplyPragmas` drops what an in-source `#pragma disable` suppresses. The order inside
      `LintsOnly`: resolve the file's imports once (`FileImports.Resolve`), the import lints, the
      per-function lints, the flow typer, the nine per-node rules in one walk (`NodeLintPass.Run`),
      then the lints that need a finished index (`FunctionResolutionLint`, `IncludeUsageLint`,
      gated on `ScriptDatabase.HasCompletedIndex`).
   3. A last cancellation check and `AnalysisGate.IsStillLive`. A superseded pass must not publish
      and must not commit.
   4. `DiagnosticsPublisher.Publish` sends `textDocument/publishDiagnostics`, stamped with the
      version that was analysed.
   5. `CommitAndScheduleDependents`: `ScriptDatabase.Commit` swaps the file's record into its
      `LanguageStore` and diffs every index. In `full` mode the record then keeps the published set
      (`WorkspaceLintSweep.KeepOnRecord`), since it is what the file reports once it closes. If the file's `ExportSignature` changed (a function
      added, renamed, re-parametered — not a body edit), `DependentDiagnosticsRefresher.Schedule`
      re-lints the other open tabs after a 900 ms debounce, and in `full` mode the closed files
      that reference what changed (`ClosedDependentsOf`, `WorkspaceLintSweep.RelintClosedFilesAsync`).
5. A pass that took longer than the debounce logs a Warning naming the file.

`didOpen` is the same path without the debounce (`ScheduleImmediateAnalysis`, on the thread pool).
It also runs the game-mismatch check once per session (`GameShapeDetector`, sending
`gscode/gameMismatch`). `didSave` analyses immediately, refreshes dependents of a saved header, and
may send `gscode/rawFolderWriteWarning` (`RawWriteGuard`). `didClose` clears the published
diagnostics and hands the file back to `WorkspaceDiagnosticsPublisher`, so a broken closed file
keeps showing its problems when the scope includes it.

---

## 3. A read request: hover, definition, references

Example: hover. Definition, references, highlight, rename and the hierarchies take the same first
three steps.

1. `HoverHandler.Handle` calls `NavigationSupport.Resolve(uri, token)`
   (`Server/Handlers/NavigationSupport.cs`). That returns the open document's latest analysis, its
   language store (`ScriptDatabase.StoreFor(language)` — `.gsc` and `.csc` never see each other), the
   context id and the declared namespaces. `ResolveFresh` is the variant that re-analyses a stale
   document first (`DocumentStore.AnalyzeIfStale`); completion and signature help use it, because
   their answer must describe the text just typed.
2. `NavigationSupport.ResolveHit` calls `SymbolAtPosition.Resolve`
   (`Workspace/Database/SymbolAtPosition.cs`): which classified reference — function, class, macro,
   field, literal, or an import path — sits under the cursor. Member hits are checked against the
   class graph.
3. The handler queries the database. For hover, `DatabaseQueries.LookupFunctions` /
   `LookupClasses` (narrowed by `DeclarationIndex` and `NamespaceIndex`, never a walk of every
   record), then `MarkdownDocRenderer` renders it. A builtin comes from `BuiltinApiSet`, a macro from
   the parse's macro table, an engine field from `ObjectFields`.
4. No reference under the cursor: keyword docs (`KeywordDocs`), then the flow typer's local type
   (`FlowTyper.TryGetLocalTypeAt`, read from the shared per-version result).
5. `LspMapping` converts Core ranges to protocol ranges. It is the only place the two type systems
   meet.

References go through `NavigationSupport.FindAllReferences`, the one query behind the reference
list, the CodeLens count and document highlight, so the three cannot disagree. A **local** variable
is not in the shared index; every position-based handler falls through to `LocalReferences` /
`LocalDefinition` for it. A **macro** key is the one exception to language isolation: it is
answered against both stores, because one header is inserted into `.gsc` and `.csc` alike.

---

## 4. Completion and signature help

1. `CompletionHandler.Handle` → `NavigationSupport.ResolveFresh`.
2. `CompletionEngine.Complete` (`Workspace/Completion/CompletionEngine*.cs`) decides the context from
   the tokens around the cursor (`.Context.cs`, no database access): inside a string literal,
   `#precache(`, a directive path, `ns::`, `owner.`, file scope, or statement scope.
3. The matching producer (`.Producers.cs`) reads the store's indexes — `VocabularyIndex` for
   literals and fields, `PathTreeIndex` for directive paths, `DeclarationIndex` for auto-import
   candidates (`LanguageStore.VisibleDeclaredNames`). Long lists are cut to the best 200 for the
   typed text and marked `Narrowed`.
4. The handler maps entries to LSP items, adds an import edit for an auto-import candidate
   (`ImportEdits.InsertionPoint`), and marks the list incomplete when any row was narrowed or needs
   an import, so the editor asks again on the next keystroke.
5. Documentation for a row arrives later through completion-resolve, keyed by the row's
   `ResolveData`.

Signature help (`SignatureHelpHandler` → `SignatureEngine.Resolve`) scans back to the open `(`,
asks the macro table first, then the script function (`CallResolution`), then the builtin, and
counts top-level commas for the active parameter.

---

## 5. Rename

1. `PrepareRenameHandler` returns the symbol range only for something the scripts define
   (`RenameHandler.IsRenameable`); builtins, engine fields and keywords get "cannot rename".
2. `RenameHandler` collects every reference through `NavigationSupport.FindAllReferences` within
   the visible context (mods never see each other, so a rename never leaks across them), checks the
   new name with `GscIdentifier` (the lexer's own rule), and returns one edit per site. A local goes
   through `LocalReferences` and is refused when the function already binds the new name.

**Renaming a file** is a separate path. The client listens to `onWillRenameFiles`, sends
`gscode/planRename` per script, and the server (`PlanRenameHandler` →
`DependencyRewrite.PlanRename`) returns the `#using` / `#insert` edits that keep importers pointing
at the moved file. It is a custom request because the OmniSharp version in use cannot see a rename's
destination.

---

## 6. Formatting

1. `DocumentFormattingHandler` → `FormattingSupport.Prepare` re-analyses the document fresh (a stale
   read here would write a corrupting edit).
2. `GscFormatter.FormatMinimalEdits` (`Server/Formatting/GscFormatter.cs`) refuses a file with lexer
   or parser errors, recomputes whitespace only, runs the aligners and `DirectiveSorter`, and
   re-lexes its own output: unless the non-trivia token stream is identical to the input's, it
   returns no edits. `fixCasing` is the one permitted token difference.
3. Edits come back per line (`LineDiff`), so the caret on an untouched line stays put. Range
   formatting keeps only edits inside the selection; on-type formatting (after `}` or `;`) keeps
   only edits inside the alignment group around the cursor (`FormatScope`).
4. On the client, `caretRestore.ts` puts carets back after a large reformat.

Every formatting rule and the corpus measurements behind it: `server/FORMATTING.md`.

---

## 7. A file changes on disk

1. The client's file watcher sends `workspace/didChangeWatchedFiles` (`**/*.gsc|csc|gsh`).
2. `WatchedFilesHandler` applies each change through `WatchedFileUpdater.Apply`
   (`Workspace/Indexing/WatchedFileUpdater.cs`): re-index created/changed files, drop deleted ones.
   A changed header invalidates its `InsertCache` entry and re-indexes every script that inserts
   it (`DatabaseQueries.ScriptsInserting`). A create or delete clears the resolver's memo
   (`InvalidateResolutionCache`), since "does this path exist" may have changed.
3. Files open in the editor are skipped: the buffer may hold unsaved edits.
4. In `full` mode, every record the update rewrote (the changed files and every file a changed
   header is inserted into) is linted again (`WorkspaceLintSweep.RelintClosedFilesAsync`), since a
   re-index stores the parse diagnostics alone.
5. The handler then refreshes closed-file problems, and names each file whose exports moved as an
   origin for `DependentDiagnosticsRefresher`, which re-lints open tabs and, in `full` mode, the
   closed files calling its functions. A deleted file has no record left to name its functions, so
   only its open dependents are refreshed.

---

## 8. Settings, workspace folders, game, cache

- **A setting changes.** `ConfigurationHandler` applies the payload (`ServerSettings.Apply`) and the
  log level live. It asks the client to refresh inlay hints when an inlay family toggled, and
  republishes workspace diagnostics when `gscode.diagnostics.scope` moved. Root and game settings
  need a reload; `client/src/reloadPrompt.ts` offers one.
- **A workspace folder is added or removed.** `WorkspaceFoldersHandler`: swap the resolver first,
  drop workspace-context records under removed folders, then index added folders (unchanged files
  restore from cache), and in `full` mode sweep again. Either way it then republishes closed-file
  problems (taking back a removed folder's) and re-lints the open tabs, as startup does.
- **The game is changed.** `gscode.selectGame` (`client/src/gamePicker.ts`) asks the server for the
  roster (`gscode/supportedGames`), writes `gscode.game` and reloads the window. The game is a
  launch argument, so nothing short of a new server process applies it.
- **Clear cache and reindex.** The client sends `gscode/clearCache`. `ClearCacheHandler` cancels the
  indexing task, drains and closes the cache, and deletes only this workspace's database; the
  client then reloads the window, which is a cold start.

---

## 9. Shutdown

`Program.cs`, after `server.WaitForExit`: cancel the indexing task and wait up to 5 s
(`IndexingLifetime.CancelAndWaitAsync`), **then** drain and close the cache
(`CacheHolder.CloseAsync`), then dispose the transport and flush the log. The order matters: closing
the cache first silently dropped every write still in flight.
