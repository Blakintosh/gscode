# GSCode.Server

The LSP host — the only project referencing OmniSharp. At P0 it is a walking skeleton:
it connects a transport, answers `initialize`/`initialized`, and honors the client's
log-level setting. Handlers arrive with their features (P4+).

## Program.cs

Top-level entry point. Configures Serilog to STDERR (stdout must stay clean for the
stdio transport; the pipe-transport client shows stderr in the "GSCode Server" output
channel) behind a `LoggingLevelSwitch`, parses transport options, connects the
transport, and starts the OmniSharp `LanguageServer` with `OnInitialize` (reads
`initializationOptions.gscode.serverLogLevel` into the level switch) and
`OnInitialized` hooks. Waits for exit, then disposes the transport owner and flushes logs.

## Transport/TransportOptions.cs

- `class TransportOptions` — CommandLineParser options: `--pipe <name>` (VSCode default),
  `--socket <port>`, `--stdio` (also the fallback when nothing is given).

## Transport/TransportResolver.cs

- `static class TransportResolver`
  - `record ResolvedTransport(Stream Input, Stream Output, IDisposable? Owner)` — the
    connected streams; `Owner` (pipe/tcp client) must be disposed on shutdown.
  - `ResolveAsync(TransportOptions, CancellationToken)` — connects the selected
    transport. Strips the Windows `\\.\pipe\` prefix VSCode puts on pipe names before
    handing the bare name to `NamedPipeClientStream`.

## Logging/ServerLogLevel.cs

- `static class ServerLogLevel`
  - `FromSetting(string?)` — maps the client's `gscode.serverLogLevel` string
    (off/error/warning/info/verbose) to a Serilog level; `off` maps to a level past
    Fatal so the channel is truly silent; unknown values fall back to info.

## Configuration/InitializationOptionsReader.cs

- `static class InitializationOptionsReader`
  - `ReadServerLogLevel(JToken)` — extracts `gscode.serverLogLevel` from the raw
    `initialize` options; returns null when the section or key is absent.

## .editorconfig

Project-local override disabling CA2007 (ConfigureAwait): OmniSharp hosts no
SynchronizationContext, so handler code stays uncluttered per the house async rules.
