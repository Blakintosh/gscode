# GSCode v2 — Server Architecture

End-to-end map of the GSCode language server and its VSCode client. Updated in every
phase that changes structure. (Phase status: **P0–P12 complete.** The full LSP feature suite is
live end to end — analysis pipeline, database + SQLite cache, navigation, completion (incl.
literals), signature help, semantic tokens, code lens, rename, call/type hierarchy, inlay hints,
type-flow inference, formatting, and code actions — plus the client command surface, docs, and
packaging. Remaining work is tracked as optional/blocked P13 follow-ups: apiUpdate.ts,
completion.fieldScope, old-CodeActionHandler mining, and the perf-corpus tooling.)

## Required toolchain

- .NET SDK 10.0.4xx (TFM `net10.0`, `LangVersion` pinned to C# 14 in `Directory.Build.props`)
- Node.js + npm for the client extension (`client/`)

## Projects and dependency flow

```
GSCode.Core  →  GSCode.Parser  →  GSCode.Workspace  →  GSCode.Server
     └──────────────────────────────────┘
```

| Project | Role | May reference |
|---|---|---|
| `src/GSCode.Core` | Neutral foundation: text positions, diagnostics, symbols, interning, the `GameProfile` portability seam, `PerfTracker` instrumentation. | nothing |
| `src/GSCode.Parser` | Pure per-file pipeline: lexer → preprocessor → parser → extraction. Deterministic; no I/O except injected providers. | Core |
| `src/GSCode.Workspace` | Script database (separate GSC/CSC stores), path/mod-overlay resolution, indexing, SQLite cache, bundled game data (`Api/`). | Core, Parser |
| `src/GSCode.Server` | The LSP host — the ONLY project referencing OmniSharp. Thin handlers over Workspace queries; protocol/domain mapping isolated in `Mapping/`. | Workspace (+ OmniSharp) |
| `tests/*` | xUnit suites, one per layer. | their subject |

Build rules (in `Directory.Build.props`): nullable enabled, warnings-as-errors,
`-p:GscodeInstrumentation=true` compiles in the `PerfTracker` timing scopes.
House style is enforced by `server/.editorconfig`; `GSCode.Server/.editorconfig`
opts that one project out of CA2007 (ConfigureAwait) per the async rules.

## Runtime shape (target design)

Per-file analysis: `SourceText → Lexer (Token[]) → Preprocessor (PToken + provenance)
→ Parser (AST records) → Extraction (ScriptRecord)`. Records land in the
`ScriptDatabase` — two independent language stores (GSC, CSC) plus a shared GSH macro
store — persisted incrementally to a per-workspace SQLite cache. LSP handlers read
immutable record snapshots; open documents keep their full `ParseResult` in the
`DocumentStore`. Path/mod-overlay questions (`share\raw` vs `mods\<name>` vs workspace)
are answered solely by the `PathResolver`.

## Language features (LSP handlers)

All handlers live in `GSCode.Server/Handlers`, each thin over a Workspace query and mapping
through `Mapping/LspMapping`. The query brain is `Database/DatabaseQueries` + `SymbolAtPosition`;
GSC/CSC isolation is enforced by resolving the store once from the asking file's language.

- **Sync + diagnostics**: incremental text sync with debounced re-analysis (`TextSyncHandler`),
  push-model `publishDiagnostics` merging parse diagnostics with cross-file lints
  (`Analysis/NamespaceUsageLint`).
- **Read**: hover (with inferred local types), definition, references (incl. literals),
  document highlight, document links, document/workspace symbols, folding, selection ranges,
  semantic tokens (full/delta/range).
- **Assist**: completion + signature help, code lens (reference counts), rename (+prepareRename),
  call and type hierarchy, inlay hints (inferred types + parameter names).
- **Edit**: formatting (whole/range/on-type) via `Formatting/GscFormatter`; code actions
  (remove-duplicate and add-missing `#using`).

Type inference is `Workspace/Typing/FlowTyper`, a small per-function forward type-flow pass over
the `ScrType` lattice, seeded with engine object-field types; it feeds inlay hints and hovers.

## The client (`client/`)

TypeScript VSCode extension (`src/extension.ts`, `server.ts`, `settings.ts`). Spawns the server
framework-dependent (`dotnet GSCode.Server.dll`) over a named pipe (stdio fallback), after
verifying the .NET 10 runtime is installed (prompting a download if missing). Carries the
GSC/CSC/GSH language registrations, TextMate grammar, semantic-token scope mapping, and
quick-suggestion defaults. Two log channels: "GSCode" (`LogOutputChannel`, extension-host
lifecycle) and "GSCode Server" (the server's stderr/Serilog). A status-bar item shows the live
indexing counter driven by `gscode/indexingStarted|Progress|Complete` notifications. Commands:
`gscode.showOutput`, `gscode.restartServer`, `gscode.openApiLibrary` (`shift+f1`), and the
`gscode.showReferences` bridge for code-lens clicks. Settings flow to the server via
`initializationOptions.gscode` and `workspace/didChangeConfiguration`.

## Dev-time tooling

`tools/field-data/` holds ALL engine field data: `sources/originals/` (verbatim game
files: ScriptObjectFields.xlsx, radiant keys.txt) and `sources/curated/` (editable
JSON source of truth). A P7 tool converts curated → the bundled runtime artifacts in
`GSCode.Workspace/Api/`.
