# GSCode.Server

The LSP host — the only project referencing OmniSharp. P4 lights up the first live
features: diagnostics while typing, the hierarchical outline, folding, selection ranges.

## Mapping/LspMapping.cs

- `static class LspMapping` — the ONLY place Core and protocol types meet: structural
  Position/TextRange conversions (both UTF-16 zero-based) and Diagnostic mapping
  (severity cast, numeric code, source "gscode").

## Configuration/ServerSettings.cs

- `sealed class ServerSettings` — the parsed gscode.* view (serverLogLevel, raw.enabled,
  rawPath/modsPath overrides, rawFileWarningMode, outline.showAssignments). `Apply(JToken)`
  merges a settings payload; missing keys keep current values.

## Configuration/ResolverHolder.cs

- `sealed class ResolverHolder` — holds the current PathResolver (starts empty; the real
  one is built at initialize when settings + workspace folders exist). Consumers read
  `Current` at call time, so swaps/rebuilds need no re-wiring.

## Handlers/TextSyncHandler.cs

- Incremental text sync. didOpen → immediate analysis; didChange → ~250 ms debounced
  re-analysis with per-document cancellation (superseded runs are cancelled, silently);
  didSave → immediate (bypasses debounce); didClose → clears diagnostics.

## Handlers/DiagnosticsPublisher.cs

- `Publish`/`Clear` — push-model publishDiagnostics wrapper.

## Handlers/DocumentSymbolHandler.cs

- The hierarchical outline: explicit `#namespace` directives become containers (the
  file-default span stays flat) → classes (members + methods) → functions →
  deduplicated assignments (behind outline.showAssignments) — plus macros literally
  #defined in the file (insert-provided ones excluded via provenance).

## Handlers/FoldingRangeHandler.cs

- Maps FoldingRegions.Compute onto LSP folding ranges (code/comment/region kinds).

## Handlers/SelectionRangeHandler.cs

- Expand-selection: the AstSearch ancestor chain per position, linked innermost→parent.

## Handlers/ConfigurationHandler.cs

- didChangeConfiguration → ServerSettings.Apply + live Serilog level switch update.

## Handlers/IndexProgressNotifier.cs

- Maps indexer progress onto the gscode/indexingStarted|Progress|Complete notifications
  (concrete record payloads), coalesced to ≤1 per ~40 ms so the status-bar counter
  races without flooding the pipe; the final count always sends.

## Handlers/WatchedFilesHandler.cs

- didChangeWatchedFiles → applies each create/change/delete via WatchedFileUpdater
  (registers **/*.gsc|csc|gsh watchers). A branch switch's whole batch applies before
  returning.

## Handlers/WorkspaceSymbolHandler.cs

- workspace/symbol — spans BOTH language stores (no asking file), matching functions and
  classes by case-insensitive substring, each result located at its file. Capped at 256.

## Handlers/NavigationSupport.cs

- `NavigationTarget` + `NavigationSupport.Resolve(uri)` — shared plumbing turning a
  document URI into its live analysis + the language store and context id to query.

## Handlers/HoverHandler.cs

- Markdown hover: script functions and builtins (fallback), classes, macros, and fields —
  rendered via MarkdownDocRenderer over SymbolAtPosition + DatabaseQueries. Field hover is
  enriched with known engine-field types (every entity kind declaring the name, since the
  owner type isn't inferred until FlowTyper).

## Handlers/DefinitionHandler.cs

- Go-to-definition: functions/classes/macros via their Definition references across the
  visible context; #using/#insert paths jump to the resolved target file.

## Handlers/ReferencesHandler.cs

- Find-all-references across the visible context (functions/classes/macros/fields and
  string/hash/istring/anim literals), honoring includeDeclaration.

## Handlers/DocumentHighlightHandler.cs

- Highlights every occurrence of the symbol under the cursor within the current file
  (definition sites as Write, others as Read).

## Handlers/DocumentLinkHandler.cs

- Turns resolved #using/#insert paths into ctrl-clickable links to their target files.

## Handlers/SemanticTokensHandler.cs

- Full-document (and delta/range via the base class) semantic highlighting; the legend
  order mirrors `SemanticTokenType`. Pushes `SemanticTokenBuilder.Build` output in order.

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
