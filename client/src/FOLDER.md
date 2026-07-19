# client/src

The VSCode extension sources. Three small files; the heavy lifting lives in the server.

## extension.ts

- `activate(context)` — entry point. Creates the "GSCode" `LogOutputChannel`
  (extension-host lifecycle only; respects VSCode's per-channel log level), builds the
  language client via `createLanguageClient`, registers commands, wires the indexing
  status bar, and starts the client. Commands: `gscode.showOutput` (opens the server
  channel), `gscode.restartServer` (restarts the language client), `gscode.openApiLibrary`
  (opens the gscode.net library for the active editor's language; bound to `shift+f1` in
  gsc/csc files), and the `gscode.showReferences` bridge for code-lens clicks.
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

- `interface GscodeSettings` — the settings payload shape shared with the server
  (currently `serverLogLevel`; grows as features land).
- `readSettings()` — reads the current `gscode.*` configuration into that shape.
