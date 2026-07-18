# GSCode.Workspace

Workspace layer: the script database (separate GSC/CSC stores), path/mod-overlay
resolution, background indexing, the SQLite cache, and the bundled game data. LSP-free.

*(Resolution = P2, Documents = P4 (below); database/indexing land P5, cache P6.)*

## Documents/DocumentStore.cs

- `sealed class OpenDocument` — one open editor file: normalized path, language, live
  SourceText, version, latest ParseResult, and the pending-analysis CTS (newer edits
  cancel in-flight debounced runs).
- `sealed class DocumentStore` — open-document tracking keyed by normalized path.
  `Open`/`Close`/`TryGet`, `ApplyChange` (LSP incremental splice or full replace), and
  `Analyze` (runs ScriptAnalysis with an insert provider bound to the file's context
  via the injected factory).

## Resolution/ResolverInsertProvider.cs

- `sealed class ResolverInsertProvider` — the real #insert provider: resolves the raw
  path through the asking file's ResolutionContext, reads and lexes the target. The
  shared lexed-GSH cache arrives with the indexer (P5).

## Resolution/IFileSystem.cs

- `interface IFileSystem` — the thin disk seam (FileExists/DirectoryExists/ReadAllText/
  EnumerateFiles) so resolver and indexer tests run on fake in-memory trees.
- `sealed class PhysicalFileSystem` — the real one.

## Resolution/RootConfig.cs

- `sealed record RootConfig` — the resolved roots: `RawRoot` (share\raw), `ModsRoot`,
  `WorkspaceFolders`. Null raw/mods roots = workspace-only mode, a first-class state.
  - `Create(rawEnabled, rawPathOverride, modsPathOverride, taToolsPath, workspaceFolders, fileSystem)`
    — rawEnabled=false forces BOTH roots null regardless of overrides or TA_TOOLS_PATH
    (explicit off wins); overrides beat the env var; roots missing on disk drop to null.

## Resolution/ResolutionContext.cs

- `enum ResolutionContextKind` — Raw / Mod / Workspace.
- `readonly record struct ResolutionContext(Kind, ModName, BaseFolder)` — a file's world,
  derived purely from its own path, with factories `RawContext`/`ForMod`/`ForWorkspace`.

## Resolution/PathResolver.cs

- `sealed class PathResolver` — the single resolution authority.
  - `GetContext(absolutePath)` — classifies by prefix: mods\<name> → Mod, share\raw →
    Raw, else Workspace (matched folder, or the file's own directory). Mods/raw win over
    a workspace match, so opening the whole tools root needs no special-casing.
  - `Resolve(context, scriptPathWithExtension)` — probes Mod: [mods\m, raw] · Raw: [raw]
    · Workspace: [base, other folders, raw]; first existing file wins. Rooted paths,
    drive letters, and ".." are rejected. Both slash styles accepted.
  - `EnumerateIndexTargets()` — every .gsc/.csc/.gsh under raw + mods + workspace
    folders, deduplicated (cold-start indexing input).

## Api/

Bundled game data, copied to the build output and loaded at runtime:

- `t7_api_gsc.json` — builtin (engine) function library for GSC: names, overloads,
  parameters, descriptions. Builtins are namespace-less in v2.
- `t7_api_csc.json` — same, for CSC.
- `t7_stock_scripts.txt` — list of script files that shipped with the mod tools;
  powers the `rawFileWarningMode = "stock"` save warning (P5).
