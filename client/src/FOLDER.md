# client/src

The VSCode extension sources. Five small files; the heavy lifting lives in the server.

## extension.ts

- `activate(context)` — entry point. Creates the "GSCode" `LogOutputChannel`
  (extension-host lifecycle only; respects VSCode's per-channel log level), builds the
  language client via `createLanguageClient`, registers commands, wires the indexing
  status bar, and starts the client. Commands: `gscode.showOutput` (opens the server
  channel), `gscode.restartServer` (restarts the language client),
  `gscode.clearCacheAndReindex` (stops the server, removes only this workspace's hashed SQLite
  database and its `-wal`/`-shm` sidecars, then reloads the window for a fresh cold index — behind
  a modal confirm), `gscode.selectGame` (the game picker, see `gamePicker.ts`),
  `gscode.openApiLibrary` (opens the gscode.net library for the active editor's
  language; bound to `shift+f1` in gsc/csc/gsh files), and the `gscode.showReferences` bridge for
  code-lens clicks.
- `registerIndexingStatusBar(context, client)` — the live indexing counter: a spinner whose
  number races upward on `gscode/indexingStarted|Progress|Complete` notifications.
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
- The roster is never held here. The client used to own that list and it drifted to nine games,
  four of them cores with no dialect, so picking one wrote a value the setting's own enum rejects
  and the server resolved it back to BO3. A request that fails is reported rather than falling
  back to a list — a fallback list IS the failure mode.
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
