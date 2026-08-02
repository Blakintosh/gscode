# GSCode Server Architecture

End-to-end map of the GSCode language server and its VSCode client. Updated whenever the structure
changes.

**The rewrite is complete.** The full LSP feature suite is live end to end — analysis pipeline,
database + SQLite cache, navigation, completion (including literals and fields), signature help,
semantic tokens, code lens, rename, call/type hierarchy, inlay hints, type-flow inference,
formatting, and code actions — plus the client command surface, snippets, docs and packaging. Five
games are supported rather than one; `GAME_PROFILES.md` says what each dialect claims and why.

Whatever is still open lives in `FOLLOWUPS.md` and nowhere else. A phase number repeated here is a
second copy of a fact that moves, which is exactly what went stale in the paragraph this replaced.

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

## Runtime shape (current implementation)

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
`gscode.showOutput`, `gscode.restartServer`, `gscode.clearCacheAndReindex`,
`gscode.openApiLibrary` (`shift+f1` in GSC, CSC, and GSH files), and the
`gscode.showReferences` bridge for code-lens clicks. Settings flow to the server via
`initializationOptions.gscode` and `workspace/didChangeConfiguration`.

## Dev-time tooling

`tools/field-data/` holds ALL engine field data: `sources/originals/` (verbatim game
files: ScriptObjectFields.xlsx, radiant keys.txt) and `sources/curated/` (editable
JSON source of truth). A P7 tool converts curated → the bundled runtime artifacts in
`GSCode.Workspace/Api/`.

## Documentation convention

`FOLDER.md` lives **one per project** (`GSCode.Core`, `GSCode.Parser`, `GSCode.Workspace`,
`GSCode.Server`, `tools/field-data`, `tests`, `client/src`) rather than one per directory, with a `##`
section per source file named by its path within the project (`## Database/ScriptRecord.cs`).
The plan said "every folder"; per-project was chosen because these projects' subfolders are
small and a reader following a type across `Database/` → `Resolution/` → `Analysis/` would
otherwise be opening four files to follow one thought. The full-dump requirement is unchanged:
every file gets either its own section or an explicit entry in a small aggregate section listing
its types and what they actually do. Partial classes and tightly-paired handlers may share one
heading (`Parser.cs (+ .Declarations / .Statements / .Expressions partials)`,
`RenameHandler.cs + PrepareRenameHandler.cs`).

`tests/FOLDER.md` is the exception to the full-dump rule: test classes listed one per section would
be unreadable, so it groups them by area with a keyword-bearing sentence each —
the point being to find the right class by searching for the construct. It also carries the
canonical list of ENVIRONMENT VARIABLES (`GSCODE_CORPUS_<GAME>`,
`GSCODE_COD4_DOCS`, `GSCODE_INSTRUMENTATION`, `GSCODE_PERF_REPORT`, and `GSCODE_SWEEP_REPORT`),
since most of them exist to point tests at game data or reports and were otherwise discoverable
only by reading fixture source.

## Known gaps

`FOLLOWUPS.md` holds only what still needs a decision, and is the single place that tracks it. The
larger items there today: modelling variadic builtins (which is what blocks restoring the upper
bound on argument counts), the opt-in `apiUpdate.ts` refresh, the optional headless CLI, and two
corpus grammar gaps consciously left alone.
