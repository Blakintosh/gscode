# GSCode.Server

The LSP host — the only project referencing OmniSharp. Every live feature is served from here:
diagnostics while typing, the hierarchical outline, folding, selection ranges, navigation,
completion, hover, signature help, code lens, rename, the hierarchies, inlay hints and formatting.

## Mapping/LspMapping.cs

- `static class LspMapping` — the ONLY place Core and protocol types meet: structural
  Position/TextRange conversions (both UTF-16 zero-based) and Diagnostic mapping
  (severity cast, numeric code, source "gscode").

## Configuration/ServerSettings.cs

- `sealed class ServerSettings` — the parsed gscode.* view (serverLogLevel, raw.enabled,
  rawPath/modsPath overrides, rawFileWarningMode, outline.showAssignments, codeLens.enabled,
  inlayHints.parameterNames, inlayHints.inferredTypes, completion.literals). `Apply(JToken)`
  merges a settings payload (accepting both dotted and nested key forms); missing keys keep
  current values.

## Configuration/ResolverHolder.cs

- `sealed class ResolverHolder` — holds the current PathResolver (starts empty; the real
  one is built at initialize when settings + workspace folders exist). Consumers read
  `Current` at call time, so swaps/rebuilds need no re-wiring.

## Handlers/TextSyncHandler.cs

- Incremental text sync. didOpen → immediate analysis; didChange → ~250 ms debounced
  re-analysis with per-document cancellation (superseded runs are cancelled, silently);
  didSave → immediate (bypasses debounce); didClose → clears diagnostics. Before publishing,
  it merges the parse diagnostics with cross-file lints (`NamespaceUsageLint`) for GSC/CSC docs.

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
  owner type isn't inferred until FlowTyper), and `.size` gets its KeywordDocs blurb. When the
  cursor is NOT on a classified reference, it renders a documented keyword/directive
  (`TryKeywordDocHover` over `KeywordDocs`: isdefined, notify, `#using`, …), then falls back to
  FlowTyper's `TryGetLocalTypeAt` to show `(local) name: type` for an inferred local variable.

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

## Handlers/CompletionHandler.cs

- Maps `CompletionEngine` entries to LSP items (kind, snippet insert text). Registers the
  trigger characters `. : # & % \ / "` so completion re-fires where it matters (the `"` fires
  literal completion inside a string). Passes the completion.literals setting through to the engine.

## Handlers/SignatureHelpHandler.cs

- Maps `SignatureEngine` results to LSP signature help; triggers on `(` and `,` (retrigger `,`).

## Handlers/CodeLensHandler.cs

- "N references" lenses above function/class declarations (counts from the reference index,
  gated by codeLens.enabled). Clicking invokes the gscode.showReferences client bridge.

## Handlers/WorkspaceFoldersHandler.cs

- `sealed class WorkspaceFoldersHandler` — handles `didChangeWorkspaceFolders` so a multi-root
  workspace needs no restart to pick up a folder. Order is load-bearing: the resolver swaps
  first (every later query classifies paths through it), records under removed folders drop
  next, and added folders index last; re-indexing runs only when something was added, and
  unchanged files restore from cache so it costs a warm start. `NextFolderSet` and
  `ShouldDropOnFolderRemoval` are pure statics so the decisions are testable without protocol
  objects — the latter drops ONLY workspace-context records, since raw and mod files stay
  reachable regardless of which folders are open. `BuildConfig` is shared with `Program.cs`, so
  a rebuild cannot drift from what initialize constructed.

## Handlers/PlanRenameHandler.cs

- `PlanRenameParams`/`PlanRenameEdit`/`PlanRenameResponse` + `sealed class PlanRenameHandler` —
  serves the custom `gscode/planRename` request, returning the `#using`/`#insert` edits a script
  rename implies. A custom request rather than the standard `willRenameFiles` handler because
  OmniSharp 0.19.9 models `FileRename` with a single `Uri` — the spec's `oldUri`/`newUri` pair
  is absent, so a server-side handler cannot learn a rename's destination. The client sources
  the event (which has both) and calls this; all path reasoning stays here via
  `Workspace/Resolution/DependencyRewrite`. Yields nothing when the file is unknown or either
  location sits outside every root.

## Handlers/RenameHandler.cs + PrepareRenameHandler.cs

- Rename functions/classes/macros across every reference in the visible context (mods can't
  see each other, so a rename never leaks across them). prepareRename returns the symbol
  range only for renameable kinds — builtins, keywords, and literals get "cannot rename".

## Handlers/CallHierarchyHandler.cs

- prepare → the function at the cursor; incoming → callers (grouped by containing function);
  outgoing → the functions called inside the body. All from the reference index.

## Handlers/TypeHierarchyHandler.cs

- prepare → the class at the cursor; supertypes → its parent (single inheritance);
  subtypes → classes whose parent is this class.

## Handlers/InlayHintHandler.cs

- Inlay hints, two independently-toggleable families over the visible range: inferred-type
  hints (`: int`) at each FlowTyper `InferredAssignment` name-range end (gated by
  inlayHints.inferredTypes), and parameter-name hints (`amount:`) before each call argument,
  resolving the callee's parameter names from the database (script functions in the file's
  namespaces, else builtins) and qualified `ns::fn` calls (gated by inlayHints.parameterNames).
  The FlowTyper it builds is seeded with the shared ObjectFields for field-type inference.
  ResolveProvider is false, so the resolve handler is a passthrough.

## Handlers/DocumentFormattingHandler.cs

- Whole-document formatting: runs `GscFormatter.FormatMinimal` over the open document and
  returns its minimal edit (common prefix/suffix trimmed). Syntax errors or an unsafe reflow
  (see the formatter's corruption guard) yield no edits.

## Handlers/DocumentRangeFormattingHandler.cs

- "Format Selection". GSC formatting is holistic, so this runs the same formatter and returns
  the minimal edit only when the changed region overlaps the requested range — formatting an
  already-clean selection does nothing.

## Handlers/DocumentOnTypeFormattingHandler.cs

- On-type formatting after `}` or `;`. Reuses the whole-document formatter's minimal edit;
  because the formatter refuses files with syntax errors, a half-typed document is left alone
  until it parses again.

## Handlers/CodeActionHandler.cs

- Quick fixes over the open document. `FindRemovableDuplicates(result, selection)` returns the
  #using directives whose (case-insensitive) path was already imported earlier and whose line
  overlaps the selection → a "Remove duplicate #using" QuickFix deleting the line.
  `FindMissingUsings(result, store, contextId, askingPath, selection)` returns the distinct
  script-relative paths (extension stripped) of visible files defining a qualified call whose
  namespace the file doesn't import (own-namespace calls and already-imported files skipped) →
  an "Add #using ..." QuickFix inserting the directive after the last existing #using (or at the
  file top). This is the natural fix for the NamespaceNotImported lint. Resolve is a passthrough.
- **Every fix carries the diagnostic it answers.** An action with no `diagnostics` is a general
  lightbulb entry: it is never presented as the fix FOR the error, Auto Fix skips it (that runs
  preferred actions only) and Fix All cannot see it. The add-#using action was produced correctly
  for a long time and still did nothing when asked for, purely because of this. `FindMissingUsingSites`
  exists to carry the call's range back out so the action can be matched to the reported 5000 —
  both come from the same `ReferenceEntry`, which is what makes the match exact rather than
  positional guesswork.
- `IsPreferred` is set only where one fix is the answer. Several possible imports means the user
  picks; an empty created declaration is never preferred, since it silences the error without the
  function doing anything.
- `UnresolvedCallFixes(uri, result, store, contextId, askingPath, diagnostic)` — the offers for
  5013/5014. Both codes get the same two, because from the fix's side they are one situation: a
  name with nothing behind it. Which code fired says where the lint LOOKED, not what to do.
  - **Create function 'name'** appends a declaration at the end of the file, opened the way the
    dialect declares one. Offered only for a call written BARE: `other::foo()` names where it
    expects the function, and declaring foo here would not put it there.
  - **Add #using X and qualify with 'ns::'** when the name exists in a namespace this file cannot
    reach. Namespace dialects only — under a merge dialect an unqualified call already resolves by
    name across the include graph, so one that reached the diagnostic is not one an import fixes.
  - Both edits hang off the fact that a call reference's range covers the NAME TOKEN alone (see
    `SymbolExtractor.RecordCalleeReference`). Qualifying is therefore an insert at the range start,
    and a wrong qualifier is replaced over the range scanned back from it.

## Formatting/GscFormatter.cs

- `FormatMinimal(ParseResult)` returns a `FormatEdit` (range + replacement) that trims the
  common leading/trailing characters so the edit spans only what changed; all three formatting
  handlers share it. `Format(ParseResult)` returns the full formatted text (or null).
- `static class GscFormatter.Format(ParseResult)` — a whitespace-only formatter. It emits
  every non-trivia token verbatim and only recomputes the surrounding whitespace: Allman
  braces, one statement per line, 4-space indent from brace/dev-block depth, padded
  control-flow and non-empty parens (`( x )`, `()` stays tight), hugging `.`/`::`/`->`/`[ ]`
  and backslash paths, and blank lines capped at two. Line breaks are forced structurally
  (Allman) but original breaks are otherwise preserved, which keeps newline-terminated
  directives (`#define`, `#if`) intact; trailing comments stay glued to their line. Two
  safety properties make corruption impossible: it refuses files with lexer (1xxx) or parser
  (3xxx) errors, and it re-lexes its own output and returns null (no edits) unless the
  non-trivia token stream is byte-for-byte identical to the input's.

## Program.cs

Top-level entry point. Configures Serilog to STDERR (stdout must stay clean for the
stdio transport; the pipe-transport client shows stderr in the "GSCode Server" output
channel) behind a `LoggingLevelSwitch`, parses transport options, connects the
transport, and starts the OmniSharp `LanguageServer` with `OnInitialize` (reads
`initializationOptions.gscode.serverLogLevel` into the level switch) and
`OnInitialized` hooks. Waits for exit, then disposes the transport owner and flushes logs.
On indexing completion it logs `Workspace indexing complete: N files in X.Xs` (info), then a
formatted `LogIndexBreakdown` block — per-language file counts (`GSC`/`CSC`/`GSH`) each split by
raw/mod/workspace context (`CategorizeContext` + `FormatLanguageLine`), and a totals line of
functions · classes · macros · distinct namespaces — and then starts `RunMemoryMonitorAsync`, a
lifetime background loop that samples the working set every 2 s and logs `Server memory: N MB`
only on >= 1 MB changes (so a stable process stays quiet).

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

## Configuration/CacheHolder.cs

- `CacheHolder` — owns the persistent cache's lifetime so handlers can reach it through DI. The
  cache opens during startup, after settings and workspace folders have arrived, which is too late
  for constructor injection — hence a holder, matching `ResolverHolder`.

## Formatting/

`FORMATTING.md` is the behaviour spec — every rule, and the measurements over the shipped scripts
that chose it. These are the pieces that implement it:

- `FormatOptions` — the knobs the formatter honours: the editor's indentation settings, which arrive
  per request in the LSP payload, plus the GSC-specific ones from configuration. The defaults are a
  fallback for callers with no editor to ask.
- `FormatScope` — which lines an on-type format may touch: the alignment GROUP around the cursor
  rather than the whole block, so a keystroke tidies what you are working on and stops there.
- `AssignmentAligner` — lines up the operators in a run of assignments at one indentation level, one
  space past the longest left-hand side.
- `ColumnAligner` — the same idea for the INTERIOR of subscripts and call arguments: a run of
  statements sharing a shape has each bracket and argument column padded to its widest.
- `DirectiveSorter` — groups and sorts the directive block at the top of a file. The formatter's one
  operation that MOVES code rather than whitespace, so it runs as a post-pass on already-reflowed
  text, after the token-stream equality gate.
- `LineFacts` — shared line-level predicates for comment tokens, leading whitespace, code-only
  tokens, and comment-only lines. Keeping these premises in one place prevents the aligners and
  formatter scope logic from disagreeing.

## Handlers/ — the remainder

- `BuiltinAtHandler` — serves the `gscode/builtinAt` request behind `shift+f1`, since the client has
  no symbol knowledge of its own and cannot tell a builtin from a script function.
- `ClearCacheHandler` — drains the cache and deletes only THIS workspace's database, server-side
  where the paths are known.
- `DependentDiagnosticsRefresher` — debounced re-linting of other open documents when an edited
  file's exported cross-file signature changes, reusing their cached parse instead of reparsing.
- `PrepareRenameHandler` — validates a rename before the UI opens: the symbol's range for anything
  the SCRIPTS define, null for what the ENGINE defines (builtins, engine fields) and for keywords, so
  the editor says "cannot rename here" instead of prompting and then failing. Shares
  `RenameHandler.IsRenameable`, so the preview and the rename cannot disagree.
- `ServerStatusNotifier` — keeps the status-bar tooltip's memory figure current. It was previously
  set once from the `gscode/indexingComplete` payload and never updated again.
- `WorkspaceDiagnosticsPublisher` — publishes problems for files that are not open, per
  `gscode.diagnostics.scope`.

## .editorconfig

Project-local override disabling CA2007 (ConfigureAwait): OmniSharp hosts no
SynchronizationContext, so handler code stays uncluttered per the house async rules.
