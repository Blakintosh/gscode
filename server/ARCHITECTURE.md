# GSCode v2 — Server Architecture

End-to-end map of the GSCode language server and its VSCode client. Updated in every
phase that changes structure. (Phase status: **P11 — Formatter + code actions (in progress).**
The full navigation/completion/lens/rename/hierarchy suite and type-flow inlay hints are live;
whole-document formatting has landed via a corruption-proof whitespace-only formatter.)

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

## The client (`client/`)

TypeScript VSCode extension. Spawns the server framework-dependent
(`dotnet GSCode.Server.dll`) over a named pipe (stdio fallback), after verifying the
.NET 10 runtime is installed. Carries the GSC/CSC/GSH language registrations, TextMate
grammar, semantic-token scope mapping, and quick-suggestion defaults. Settings flow to
the server via `initializationOptions.gscode` and `workspace/didChangeConfiguration`.

## Dev-time tooling

`tools/field-data/` holds ALL engine field data: `sources/originals/` (verbatim game
files: ScriptObjectFields.xlsx, radiant keys.txt) and `sources/curated/` (editable
JSON source of truth). A P7 tool converts curated → the bundled runtime artifacts in
`GSCode.Workspace/Api/`.
