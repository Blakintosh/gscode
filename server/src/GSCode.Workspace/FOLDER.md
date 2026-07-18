# GSCode.Workspace

Workspace layer: the script database (separate GSC/CSC stores), path/mod-overlay
resolution, background indexing, the SQLite cache, and the bundled game data. LSP-free.

*(Resolution = P2, Documents = P4, Database/Indexing = P5 (all below); cache lands P6;
Typing lands P10.)*

## Database/ScriptRecord.cs

- `MacroRecord(Name, IsFunctionLike, Parameters, NameRange, Documentation)` — a macro
  surfaced from a file (exact-case name; bodies stay parser-side).
- `DependencyEdge(RawPath, ResolvedPath, IsInsert, Range)` — one #using/#insert edge.
- `sealed record ScriptRecord` — the complete immutable knowledge about one file:
  path (the database key), language, ContextId ("raw"/"mod:x"/"workspace:f"),
  RelativePath (the overlay-shadowing identity), content hash, namespaces, functions,
  classes, macros, dependencies, references, diagnostics, IsDirty (unsaved editor
  state, never persisted). Closed files keep ONLY this record.

## Database/LanguageStore.cs

- `sealed class LanguageStore` — ONE language world: path-keyed record map + its
  ReferenceIndex. Upsert swaps records atomically and diffs the index; GSC/CSC
  isolation is two instances of this class, never a filter.

## Database/ReferenceIndex.cs

- `sealed class ReferenceIndex` — the inverted key→files index. One lock, held per
  file-diff; exact ranges come from scanning the named files' reference lists.

## Database/ScriptDatabase.cs

- `sealed class ScriptDatabase` — the façade: `Gsc`/`Csc` stores + the shared GSH
  record map (headers serve both worlds). `Commit` builds and stores a record from a
  ParseResult; `BuildRecord` is the pure builder (macros filtered to file-local,
  dependency edges from inserts + usings, xxHash64 content hash). `CanSee` encodes the
  visibility rule (raw←raw; mod M←{M,raw}; workspace←{workspaces,raw}); `ContextIdOf`
  stringifies contexts.

## Database/DatabaseQueries.cs

## Completion/CompletionEntry.cs

- `CompletionKind` + `CompletionEntry` — the LSP-free completion suggestion model.

## Completion/GscKeywords.cs

- `static GscKeywords` — the statement-scope and top-level keyword lists offered in completion.

## Completion/CompletionEngine.cs

- `sealed class CompletionEngine.Complete(result, contextId, position)` — context-aware
  completion driven by the tokens around the cursor: `#precache(` asset types,
  `#using`/`#insert` path segments, `ns::` (that namespace's functions only), `owner.`
  fields (+ `.size`), and statement/top-level scope (keywords, file macros, namespace
  functions, visible classes, namespace-less builtins as call snippets).

## Completion/SignatureEngine.cs

- `SignatureParameter`/`SignatureResult` + `SignatureEngine.Resolve(...)` — scans back from
  the cursor to the enclosing '(', identifies the callee (script function / builtin) and
  the active parameter (top-level comma count), and renders the signature + parameter docs.

## Database/SymbolAtPosition.cs

- `HitKind` + `PositionHit` + `static SymbolAtPosition.Resolve` — the one resolver behind
  hover/definition/references/highlight/documentLink: finds the classified reference
  (function/class/macro/field/literal) or #using/#insert dependency path at a position,
  working from either a stored ScriptRecord or an open document's live ParseResult.

## Database/DatabaseQueries.cs

- `ResolvedFunction`/`ResolvedClass` + `static DatabaseQueries` — context-filtered
  lookups that MERGE namespaces across contributing files, hide private functions from
  other files, and apply overlay shadowing (same RelativePath: overlay beats raw);
  `FindReferences` returns visible (record, entry) pairs for a key.

## Indexing/WorkspaceIndexer.cs

- `IndexingMode` (Off/Partial/Full) + `IIndexProgressListener` (+Null impl) —
  the server maps listener events onto gscode/indexing* notifications.
- `sealed class WorkspaceIndexer` — cold start: enumerate targets → bounded
  `Parallel.ForEachAsync` (cores−1) running the per-file pipeline → Commit records.
  Reads the current resolver via an injected `Func<PathResolver>` so resolver swaps take
  effect immediately. A `ConcurrentDictionary<path, Lazy<InsertedFile?>>` lexes each GSH
  exactly once no matter how many scripts insert it; `InvalidateGsh` drops one on change.
  `IndexFile` is the single-file path the watcher reuses. `UseCache` enables cold-restore:
  the two-pass `IndexAsync` restores files whose on-disk content hash matches the cached
  record (skipping the parse), then re-parses restored files that #insert a header which
  itself changed (phase two), and write-throughs every fresh analysis to the cache.
  `RemoveFile` drops a deleted file from the database, the cache, and the GSH lex cache.

## Cache/CacheSchema.cs

- `static CacheSchema` — SchemaVersion + RecordFormatVersion (the hand-bumped gates),
  the meta keys, and the `meta`/`files`/`deps` table DDL. Either version mismatch (or a
  build-identity mismatch) wipes the cache; there are no migrations.

## Cache/ServerBuildIdentity.cs

- `static ServerBuildIdentity.Compute(dataFilePaths)` — a SHA-256 fingerprint of the
  engine assembly MVIDs + the bundled data-file hashes. Any rebuild that could change
  analysis output changes this, invalidating the cache automatically.

## Cache/RecordSerializer.cs

- Source-generated STJ context (`CacheJsonContext`) + `Serialize`/`Deserialize` — a
  ScriptRecord to/from a gzipped JSON blob (no runtime reflection). Deserialize returns
  null on a corrupt blob so one bad row never fails the restore.

## Cache/SqliteCache.cs

- `sealed class SqliteCache : IAsyncDisposable` — the per-workspace cache.
  `ResolveDatabasePath` (→ %APPDATA%/gscode/cache/&lt;hash&gt;.db), `CleanUpLegacyCache`
  (deletes the old single-file gzip-JSON cache), `Open` (WAL + busy_timeout, creates
  tables, wipes on version/identity mismatch), `LoadAll` (cold-restore input),
  `Enqueue`/`EnqueueDelete` (never block — a single background writer drains a bounded
  channel, coalescing batches into transactions; dirty records are skipped), and
  `DisposeAsync` (drains the writer + checkpoints so a clean exit loses nothing).

## Indexing/WatchedFileUpdater.cs

- `WatchedFileChange` (Created/Changed/Deleted) + `sealed class WatchedFileUpdater` —
  applies on-disk changes to the database: re-index created/changed files, drop deleted
  ones, and when a GSH changes invalidate its lex cache and re-index every file that
  #inserts it (via `ScriptDatabase.FilesInserting`) so macro edits propagate. Returns
  the touched paths for diagnostic republishing.

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

## Typing/FlowTyper.cs

- `readonly record struct InferredAssignment(NameRange, Type, Name)` — the inferred `ScrType`
  of a local at its assignment site (with its display-case name), consumed by the inlay-hint
  handler and the hover lookup.
- `readonly record struct LocalTypeHover(Name, Range, Type)` — the inferred type of the local
  identifier under a cursor, consumed by hover.
- `sealed class FlowTyper` — a deliberately-small forward type-flow pass, per function.
  `InferAssignments(ParseResult)` walks each function/method body with a per-function
  local environment (`name → ScrType`), recording the FIRST assignment of each local that
  resolves to a concrete type; later assignments update the environment but never add a
  second hint. `TryGetLocalTypeAt(result, position)` finds the innermost identifier under a
  cursor and its enclosing function (via `AstSearch.ChainAt`) and returns the local's inferred
  type when one exists — so a hover always agrees with the inlay hint at the assignment.
  `TypeOf` types literals, parenthesised/vector/array/`new` expressions,
  identifiers (earlier locals, then the globals `self`/`level`/`world`/`anim`/`game`),
  prefix ops (`!`→bool, `&`→function, `~`→int, `-`→numeric), binary ops (comparisons and
  logicals→bool, `+` string-concatenation vs numeric widening, shifts/bitwise→int),
  builtin call return types via `MapReturnType`, and field access `owner.field` (`.size`→int;
  else the engine object-field data seeds a type, but only when every entity kind declaring
  the field name agrees — the owner's kind isn't inferred). Anything uncertain stays `Unknown`
  and produces no hint — the zero-false-positive rule. Script-function return inference is out
  of scope (their bodies aren't re-typed here). Constructed with the per-language `BuiltinApi`
  and the shared `ObjectFields`.

## Api/

Bundled game data (copied to the build output) plus the loaders and doc renderer.

- `t7_api_gsc.json` / `t7_api_csc.json` — builtin (engine) function libraries;
  namespace-less in v2. `t7_stock_scripts.txt` — shipped-file list for the stock warning.
- `t7_object_fields.json` / `t7_radiant_keys.json` — engine object-field types (by entity
  kind) and radiant map-entity KVP keys, generated by `tools/field-data` from the curated
  sources. Loaded by `ObjectFields`.
- `BuiltinApi.cs` — `BuiltinFunction`/`BuiltinOverload`/`BuiltinParameter` model + the
  case-insensitive per-language library (`Find`, `All`).
- `ApiLoader.cs` — source-generated STJ DTOs + `Load(apiDir, language)` mapping the JSON
  to the clean model; missing/corrupt files yield an empty library.
- `BuiltinApiSet.cs` — both languages' libraries; `For(language)` selects one.
- `MarkdownDocRenderer.cs` — the one hover/completion/signature renderer:
  `RenderFunction` (script functions: prototype + ScriptDoc summary/region/params/
  examples), `RenderBuiltin` (prototype + description + overloads + example),
  `RenderMacro` (#define form + trailing-comment doc).
- `ObjectFields.cs` — `ObjectField`/`RadiantKey` model + `ObjectFields.Load(apiDir)`;
  `FindField(name)` returns every entity kind declaring that field (owner type isn't
  inferred until FlowTyper), `FindRadiantKey(name)` returns the map key. Source-gen JSON.
