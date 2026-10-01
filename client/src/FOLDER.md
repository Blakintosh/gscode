# client/src

The VSCode extension sources. Five small files; the heavy lifting lives in the server.

## extension.ts

- `activate(context)` — entry point. Creates the "GSCode" `LogOutputChannel`
  (extension-host lifecycle only; respects VSCode's per-channel log level), builds the
  language client via `createLanguageClient`, registers commands, wires the indexing
  status bar, and starts the client. Commands: `gscode.showOutput` (opens the server
  channel), `gscode.restartServer` (restarts the language client),
  `gscode.clearCacheAndReindex` (behind a modal confirm, asks the server over `gscode/clearCache` to
  stop indexing and delete only this workspace's database — the server is the side that knows which
  file that is — then reloads the window for a fresh cold index), `gscode.selectGame` (the game picker, see `gamePicker.ts`),
  `gscode.openApiLibrary` (opens the gscode.net library for the active editor's
  language; bound to `shift+f1` in gsc/csc/gsh files), and the `gscode.showReferences` bridge for
  code-lens clicks.
- `registerIndexingStatusBar(context, client)` — the live indexing counter: a spinner whose
  number races upward on `gscode/indexingStarted|Progress|Complete` notifications.
- `registerRenameDirectiveFixup(context, client, log)` — on `onWillRenameFiles`, asks the server
  (`gscode/planRename`) for the `#using`/`#insert` edits a script move implies and applies them with
  the rename, de-duplicating an edit planned twice for a `.gsc`/`.csc` pair.
- `registerSemicolonDeduplication(context)` — removes the second of two adjacent semicolons right
  after one is typed, so typing `;` over the one a call completion already inserted "types over" it,
  as `editor.autoClosingOvertype` does for `)`. Client-side and unconditional, because the server's
  on-type handler runs only when `editor.formatOnType` is on.
- `deactivate()` — stops the language client.

## server.ts

- `isDotnetRuntimeAvailable()` — runs `dotnet --list-runtimes` and checks for the
  required Microsoft.NETCore.App major version (10).
- `resolveServerFolder(context)` — packaged builds use the bundled `service/` folder;
  debug sessions (`VSCODE_DEBUG`) read `DEBUG_SERVER_LOCATION`/`SERVER_LOCATION` from
  `client/.env` (see `.env.example`).
- `createLanguageClient(context, log)` — verifies the runtime (prompting a .NET download
  when missing), then builds the `LanguageClient` that spawns `dotnet GSCode.Server.dll`
  over a named pipe. Creates the "GSCode Server" output channel, which receives the
  server's stderr (Serilog). Sends `initializationOptions.gscode` from `readSettings()`.

## caretRestore.ts

- `caretRestoreMiddleware` — language-client middleware for Format Document and Format Selection
  (and so format-on-save). Passes the server's edits through unchanged, and puts every caret back
  where its position went once VS Code has applied them. Needed because VS Code's text buffer,
  handed a thousand edits or more, replaces the whole span they cover in one operation, and a caret
  inside it lands at its end: a large reformat sent the caret from line 2399 to line 7284.
- `mapOffset(document, offset, edits)` — where a document offset ends up after the edits: shifted
  by every edit before it, or moved to the end of an edit containing it, as VS Code does.

## settings.ts

- `interface GscodeSettings` — the settings payload shape shared with the server: log level,
  indexing mode, cache, the raw/mods paths and their enable flag, the raw-file warning mode, and
  the per-feature toggles (outline assignments, code lens, both inlay-hint kinds, literal
  completion, completion punctuation, diagnostics scope, formatter knobs).
- `readSettings()` — reads the current `gscode.*` configuration into that shape.

## gamePicker.ts

- `pickGame(client, log, options)` — the `gscode.selectGame` command. Asks the server for the
  roster over `gscode/supportedGames`, shows it with the RUNNING game ticked, writes
  `gscode.game`, and offers the reload that applies it.
- `pickGameFrom(games, selected, log, title)` — the same picker for a roster already in hand,
  which is what `gscode/gameMismatch` carries.
- The roster is never held here. A client-side copy drifts — one reached nine games, four of them
  cores with no dialect, so picking one wrote a value the setting's own enum rejects and the server
  resolved it back to BO3. A request that fails is reported rather than falling back to a list — a
  fallback list IS the failure mode.
- The tick follows `GameProfile.Active`, not `gscode.game`. The two differ exactly when something
  is wrong, and ticking a game that is not in use rules out the thing being looked for.
- A window reload, not `gscode.restartServer`: the game is a command-line argument and the launch
  arguments are captured when the client is constructed, so a restart relaunches with the game the
  session started with.

## reloadPrompt.ts

- `registerReloadPrompt(...)` — offers a window reload when a change needs one to take effect,
  rather than leaving the editor in a state the server can no longer describe.
- `suppressReloadPromptOnce()` — skips the next prompt, for a command that writes one of those
  settings and prompts about it itself. The game picker is the only caller; without it, picking a
  game produces two notifications about one edit.
