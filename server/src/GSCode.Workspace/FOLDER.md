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

## Completion/CompletionEntry.cs

- `CompletionKind` + `CompletionEntry` — the LSP-free completion suggestion model.

## Completion/GscKeywords.cs

- `static GscKeywords` — the statement-scope and top-level keyword/directive lists offered in
  completion (assert/assertmsg excluded — they come from the builtin API instead). Documented
  entries get their KeywordDocs blurb as the completion's documentation.

## Completion/CompletionEngine.cs

- `sealed class CompletionEngine.Complete(result, contextId, position, includeLiterals)` —
  context-aware completion driven by the tokens around the cursor: inside a
  `"..."`/`&"..."`/`#"..."` literal it offers the known literals of that kind from the visible
  reference index (gated by `includeLiterals` = the completion.literals setting; disabled →
  nothing, since statement scope makes no sense in a string); otherwise `#precache(` asset types,
  `#using`/`#insert` path segments, `ns::` (that namespace's functions only), `owner.` fields
  (+ `.size`), and statement/top-level scope (keywords, file macros, namespace functions, visible
  classes, namespace-less builtins as call snippets).

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
  lookups that MERGE namespaces across contributing files and apply overlay shadowing
  (same RelativePath: overlay beats raw); `FindReferences` returns visible (record, entry)
  pairs for a key. Private functions follow NAMESPACE privacy, not file privacy — a
  namespace can be split across files, so any file declaring it may call in; callers pass
  their namespaces via `askingNamespaces` (`DeclaredNamespaces(result)` reads them from the
  live parse result so unsaved edits count), and one that cannot falls back to same-file
  visibility. `FindGshReferences` is the deliberate language-guard exception: a `.gsh` serves
  both languages, so macros declared in headers live in the shared GSH store and are
  unreachable from either LanguageStore.

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

- `sealed record RootConfig` — the resolved roots: `RawRoot`, `ModsRoot`, `WorkspaceFolders`. Null
  raw/mods roots = workspace-only mode, a first-class state.
  - `Create(rawEnabled, rawPath, modsPath, workspaceFolders, fileSystem)` — both roots come from
    settings and nowhere else. rawEnabled=false forces BOTH null whatever the paths say (explicit
    off wins), and a path naming a folder that is not on disk drops to null rather than being
    trusted. Nothing is read from the environment, so one code path serves every game.

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

## Analysis/NamespaceUsageLint.cs

- `static NamespaceUsageLint.Analyze(result, store, language, resolver, askingPath)` — a
  cross-file lint: a qualified call `ns::foo()` should have a `#using` that imports a file
  declaring namespace `ns` (or `ns` be one of the file's own namespaces). Returns Warning
  diagnostics (`NamespaceNotImported`). Zero false positives by construction: it builds the set
  of available namespaces from the file's own `#namespace` blocks plus every `#using` target
  resolved to an INDEXED record, and if any `#using` can't be resolved to a record it suppresses
  the whole lint (a not-yet-known import might supply the namespace). Unqualified calls key under
  the current namespace so they never trip it; `sys::` builtin calls have a null namespace and
  are skipped. Merged into open-document diagnostics by the server's TextSyncHandler.

## Analysis/UnusedUsingLint.cs

- `static UnusedUsingLint.Analyze(result, store, language, resolver, askingPath)` — flags a
  `#using` whose target contributes nothing the file uses, as a Hint tagged `Unnecessary` so
  the directive greys out. Deliberately conservative, since deleting a working import is far
  worse than missing a stale one: three separate rules keep an import alive — it declares a
  referenced function or class, it contributes a namespace some qualified reference mentions
  (namespace merging means the called function may live in a sibling file), or it declares an
  `autoexec` (the file is imported purely for side effects and legitimately references
  nothing). One unresolvable `#using` suppresses the whole pass.

## Analysis/PreferBooleanLiteralLint.cs

- `static PreferBooleanLiteralLint.Analyze(result, builtins)` — hints that a literal `0`/`1`
  passed to a builtin parameter declared `bool` should be `false`/`true`. Scoped to
  declared-bool parameters ONLY: an int parameter legitimately takes 0 and 1, and flagging
  those was the v1 bug this rule's original test existed to pin. Every overload must agree the
  parameter is bool, since which overload the author meant is unknowable here.

## Analysis/PrivateAccessLint.cs

- `static PrivateAccessLint.Analyze(result, store, contextId, askingPath, builtins)` — reports
  a call to a function that exists but is private to a namespace the calling file does not
  declare, turning a silent resolution failure into its actual reason. Fires only when the
  normal lookup finds nothing AND a privacy-ignoring lookup finds a private declaration
  elsewhere; builtin names are skipped so a same-named private script function cannot make a
  working builtin call look broken. Carries related information pointing at the declaration.

## Analysis/ReadOnlyWriteLint.cs

- `static ReadOnlyWriteLint.Analyze(result, objectFields)` — reports writes to `.size` (Error;
  a language-spec fact) and to engine fields the curated data marks read-only (Warning, since
  that data can carry mistakes). Assignments including compound forms and `++`/`--` all count
  as writes. A field is only flagged when EVERY entity kind declaring the name agrees it is
  read-only, because the owner's kind isn't inferred at this layer.

## Resolution/RawWriteGuard.cs

- `RawFileWarningMode` + `static RawWriteGuard` — decides whether saving a file deserves the
  raw-folder warning. `ParseMode` maps the client setting, falling back to `stock`. `ShouldWarn`
  protects only the raw context: mod and workspace files never warn even in `all` mode, because
  shadowing a stock script from a mod is the correct workflow.

## Resolution/DependencyRewrite.cs

- `DependencyEdit` + `static DependencyRewrite` — plans the `#using`/`#insert` path edits a file
  rename implies, so renaming a script does not silently break its importers. `PlanRename`
  matches on the path AS WRITTEN rather than a resolved absolute path, because `#using` edges
  carry no resolved path (they resolve lazily per asking context, so the same text can mean
  different files in different contexts). Scans both language stores plus the shared headers,
  since a `.gsh` can insert another. `ToDirectivePath` encodes the asymmetry that `#using` names
  a script without its extension while `#insert` keeps the `.gsh`.

## Typing/BuiltinEmulations.cs

- `static BuiltinEmulations.TryGetReturnType(name, out type)` — return types for the callable
  KEYWORDS, which carry no entry in the bundled API and would otherwise type as Unknown.
  Deliberately two entries: of the keywords absent from the API, only `isdefined` (bool) and
  `vectorscale` (vector) yield a value worth typing; the rest are statement-shaped, and in this
  lattice a void result is indistinguishable from Unknown.

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
- `KeywordDocs.cs` — `static KeywordDocs.Find(word)` — documentation (from the GSC language PDF)
  for the evaluation/function-usage keywords (wait, waittill, notify, endon, isdefined,
  vectorscale, profilestart/stop, `.size`) and the preprocessor directives (keyed with their
  leading `#`). Powers keyword/directive hover and completion detail. assert/assertmsg/gettime are
  deliberately absent — they are engine builtins served by the API library, not keywords.
- `ObjectFields.cs` — `ObjectField`/`RadiantKey` model + `ObjectFields.Load(apiDir)`;
  `FindField(name)` returns every entity kind declaring that field (owner type isn't
  inferred until FlowTyper), `FindRadiantKey(name)` returns the map key. Source-gen JSON.
