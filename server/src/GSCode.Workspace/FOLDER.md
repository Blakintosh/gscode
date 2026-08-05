# GSCode.Workspace

Workspace layer: the script database (separate GSC/CSC stores), path/mod-overlay
resolution, background indexing, the SQLite cache, and the bundled game data. LSP-free.

Everything below is built. The folders map to the layers: `Resolution/` turns a script path into a
file, `Documents/` holds open-buffer state, `Database/` and `Indexing/` own the record store,
`Api/` the bundled game data, `Analysis/` the lints, `Completion/` and `Typing/` the information
surfaces.

## Database/ScriptRecord.cs

- `MacroRecord(Name, IsFunctionLike, Parameters, NameRange, Documentation)` — a macro
  surfaced from a file (exact-case name; bodies stay parser-side).
- `DependencyEdge(RawPath, ResolvedPath, IsInsert, Range)` — one #using/#insert edge.
- `sealed record ScriptRecord` — the complete immutable knowledge about one file:
  path (the database key), language, ContextId ("raw"/"mod:x"/"workspace:f"),
  RelativePath (the overlay-shadowing identity), content hash, namespaces, functions,
  classes, macros, dependencies, references, diagnostics, IsDirty (unsaved editor
  state, never persisted). Closed files keep ONLY this record.

## Database/ClassGraph.cs

- `ClassGraph` — the per-language reverse index of class declarations, parent links, and method
  names. Replaces repeated workspace-wide scans with path-valued buckets that can be updated or
  removed exactly when one file changes.

## Database/ExportSignature.cs

- `static ExportSignature` — hashes only the cross-file surface a record exposes: namespaces,
  functions, classes, methods, parameters, and visibility/dev-only flags. Body edits therefore do
  not fan out diagnostics, while changes another file can observe invalidate dependents.

## Database/LanguageStore.cs

- `sealed class LanguageStore` — ONE language world: path-keyed record map + its
  ReferenceIndex, DeclarationIndex and ClassGraph. Upsert swaps records atomically and diffs all
  three under one write gate; GSC/CSC isolation is two instances of this class, never a filter.

## Database/ReferenceIndex.cs

- `sealed class ReferenceIndex` — the inverted key→files index. One lock, held per
  file-diff; exact ranges come from scanning the named files' reference lists.

## Database/DeclarationIndex.cs

- `sealed class DeclarationIndex` — the name→declaring-files index, the counterpart to
  `ReferenceIndex`. Maintained by the same Apply-on-upsert diff under the store's write gate, and
  holds PATHS rather than records for the same reason: a record is swapped wholesale on every edit,
  so holding one would pin a stale version. Keyed on `FunctionSymbol.KeyName` and compared ordinally
  — exactly the comparison `LookupFunctions` performs, so the candidate set is identical.
- It exists because `LookupFunctions` used to walk every record and every function in each (~30,000
  symbols on BO3) once per CALL SITE, which made four lints 97% of the cross-file lint cost. It
  narrows WHERE to look and decides nothing: visibility, namespace, privacy and overlay shadowing
  all still apply after it. See `PERF.md`.

## Database/FunctionLookupCache.cs

- `sealed class FunctionLookupCache` — a memo over `LookupFunctions` for the span of ONE file's
  analysis. Scripts call the same handful of names repeatedly, so the same question was asked dozens
  of times per file. Per file and discarded with it, deliberately: a longer-lived cache would need
  invalidating on every edit anywhere in the workspace, since an unqualified call under a merge
  dialect resolves by name across everything indexed — a subscription problem, not a dictionary.

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
  (+ `.size`), and statement/top-level scope (keywords, the dialect's global objects and snippets,
  file macros, namespace functions, visible classes, namespace-less builtins as call snippets).

## Completion/GscSnippets.cs

- The snippets whose construct only SOME dialects have — `foreach`, `class`, `new`, the BO3
  function modifiers, every import directive, and the two ScriptDoc forms. They cannot be
  contributed by the extension: a contributed snippet is registered per language id, one id covers
  five games, and VS Code merges them in unconditionally with no way to withdraw one. That is how
  CoD4 came to be offered a `foreach` loop it cannot run.
- Each entry is gated on a keyword or directive passed to `GscKeywords.IsAvailable`, so a snippet
  and the word it writes cannot disagree about which games have it. The ScriptDoc pair is the
  exception, gated on `ScriptDocStyle` because neither form is a word.
- The UNIVERSAL snippets stay in `client/snippets/common.json`, where they cost nothing and work
  before the server has started. The function declaration is neither: `FunctionDeclarationSnippet`
  builds it per dialect, since the merge games declare with a bare name.

## Completion/SignatureEngine.cs

- `SignatureParameter`/`SignatureResult` + `SignatureEngine.Resolve(...)` — scans back from
  the cursor to the enclosing '(', identifies the callee (script function / builtin) and
  the active parameter (top-level comma count), and renders the signature + parameter docs.

## Database/SymbolAtPosition.cs

- `HitKind` + `PositionHit` + `static SymbolAtPosition.Resolve` — the one resolver behind
  hover/definition/references/highlight/documentLink: finds the classified reference
  (function/class/macro/field/literal) or #using/#insert dependency path at a position,
  working from either a stored ScriptRecord or an open document's live ParseResult.

## Database/LocalDefinition.cs

- `static LocalDefinition` — resolves a local variable or parameter to its introducing declaration
  within the enclosing function. Locals stay outside the shared reference index, so this AST-based
  lookup prevents same-named variables in unrelated functions from colliding.

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
- `LookupFunctions` asks `DeclarationIndex` for its candidate files rather than scanning every
  record; everything it does with them is unchanged. `IncludeClosure` walks the `#include` graph
  TRANSITIVELY — the compiler flattens the chain, which the corpus settled — and reports whether the
  walk saw everything, since a rule may only assert a name is out of scope against a complete one.
  The direct-only helpers beside it (`FunctionsInIncludeScope` and friends) stay narrow on purpose:
  completion offering too little is harmless where an Error is not.

## Database/MethodResolution.cs

- `ClassMethod` / `static MethodResolution` — canonicalizes class-method call keys across bare,
  qualified, inherited, and unknown-receiver arrow calls. It supplies the shared method surface
  for completion/signature help/hover and the reference union used by definitions and code lenses.

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

- `static ServerBuildIdentity.Compute(dataFilePaths, game)` — a SHA-256 fingerprint of the
  active game + the engine assembly MVIDs + the bundled data-file hashes. Any rebuild that
  could change analysis output changes this, invalidating the cache automatically. The game
  is in the material explicitly: a record is dialect-specific, and restoring one game's into
  another's session is undetectable downstream. It used to invalidate only because each game
  bundles differently-named data files, which MW2 (no data at all) already broke.

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
  - `Create(rawEnabled, rawPath, modsPath, workspaceFolders, fileSystem, profile?)` — configuration
    first, derivation second. A configured path that exists on disk is used verbatim; one naming a
    missing folder is dropped rather than trusted, since a root under which every lookup misses
    reports the user's scripts as broken instead of the setting. Whatever is left unset is derived
    by `FindRootAbove`, walking up from each workspace folder probing for the profile's
    `RawSubfolder` / `ModsSubfolder`. rawEnabled=false forces BOTH null by either route — explicit
    off beats configuration and derivation alike. Nothing is read from the environment.
  - `FindRootAbove(startFolders, subfolder, fileSystem)` — each start folder is exhausted to the
    drive before the next is tried, so an earlier workspace folder wins outright rather than losing
    to a shallower match under one the user listed second.

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
  declaring namespace `ns` (or `ns` be one of the file's own namespaces). Returns Error
  diagnostics (`NamespaceNotImported`) — the script does not link without the import, so it is a
  broken build rather than a style point; it ran as a Warning first while the rule proved itself
  and was promoted after holding at zero across the stock corpus. Zero false positives by construction: it builds the set
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

## Analysis/ — the remaining lints

Each is a `static Analyze(...)` returning diagnostics, run per open document and merged by the
server's `TextSyncHandler`. Severity is chosen by MEASUREMENT over the corpus, not by taste: a rule
reported as an Error must never land on code that ships and works.

- `FunctionResolutionLint` (5013/5014/5025) — a call resolving to no script function and no builtin.
  Splits script from builtin so a corpus sweep of 5014 yields the candidate list for curating the
  builtin library. Stands down on a game with no builtin data, and where the library is known
  incomplete. 5025 is 5014's last branch rather than a rule of its own: a name that is a KEYWORD in
  a later game of the lineage (`foreach` under CoD4) arrives here as a call, because the lexer gates
  keywords per profile and leaves the word an identifier. It sits behind the same builtin gate, so
  it changes what a reported call is CALLED and never where the lint speaks.
- `IncludeUsageLint` (5026) — the `#include` counterpart to `NamespaceUsageLint`: an unqualified call
  to a function that EXISTS but that nothing merges into scope. Resolution finds it anyway — a merge
  dialect keys functions `(null, name)` and `LookupFunctions` searches every visible record, so
  hover and go-to-definition keep working while the import is missing — which is exactly why 5013/5014
  stay silent here and this rule is needed. Scope comes from `DatabaseQueries.IncludeClosure`, which
  follows the graph TRANSITIVELY because the compiler flattens the chain; the corpus settled it, since
  direct-hops-only reported 36 stock calls, `maps\_createpath.gsc` reaching `flag_init` through
  `maps\_utility` among them. Gated on `GameProfile.HasTrustedEngineNames`, since a name that is an
  undocumented engine function AND a script function elsewhere would otherwise be blamed on the user.
  Measurement drew that line: CoD4 qualifies on its own library (the sweep found one gap, `abs`, now
  curated in), MW2 ships no library and borrows CoD4's NAMES, and WaW and BO1 qualify for neither —
  with the gate lifted they report 204 and 387, mostly engine functions their own libraries lack.
- `AmbiguousFunctionLint` (5007) — one name reachable as several distinct declarations.
- `FileImports` — a file's import directives resolved ONCE, shared by the four lints that each used
  to walk the directives, resolve every path, normalize and `store.TryGet` again. On a BO3 file that
  was the same `#using` list resolved three times per keystroke. `Complete` carries the bail-out all
  four share; `Usings` and `Includes` stay APART because no dialect has both and one list would let
  the include rule judge a `#using`. `UsingNotFoundLint` deliberately does NOT share it — it asks
  whether the target exists on DISK, which is what decides linking, while this also requires the
  index to have reached it.
- `ImportGate` — the precondition several lints share: an unresolved `#insert` or `#using` makes the
  set of legal names unknowable, so a rule about to say "this matches nothing" stands down. The
  caller names which codes matter.
- `ArgumentCountLint` (5022/5023) — the rule is NOT symmetric. A **script function** is only wrong
  with too MANY arguments: passing fewer is legal and idiomatic, the rest being `undefined`. A
  **builtin** is engine-validated, so its mandatory count is a real lower bound — but only where
  `HasReliableBuiltinSignatures` says the data can carry the claim. The upper bound is absent on
  builtins because the library under-declares variadics; restoring it is a data problem, not a code
  one.
- `CaseLabelLint` (5010/5011/5017) — a `case` on an undefined value, a non-constant label, and the
  same label twice in one switch. The third found a real duplicate `case 1:` in shipped BO3 code.
- `ClassCycleLint` (5021) — a class inheritance cycle, which would otherwise recurse forever.
- `DevBlockCallLint` (5006) — calling a `/# #/`-only function from release code.
- `DuplicateImportLint` (5018) — the same file imported twice, tagged `Unnecessary` so the line
  greys out. Separator and case differences do not make it a different file.
- `UnassignedVariableLint` (5016/5024) — a local read that nothing in the function writes. Excludes
  parameters, loop bindings, `waittill` outputs, profile globals, file-scope constants, macro-supplied
  names, and the `...` parameter pack; reports 5024 instead when the pack is read in a function that
  does not declare `...`. An unresolved import stands the whole rule down.
- `UnreachableCodeLint` (5015) — statements after a `return`/`break`/`continue`.
- `UnusedBindingLint` (5020) — a parameter or `waittill` output nothing reads. A **Hint**, so it
  never reaches the Problems panel and the fade is the entire output: at any panel-visible severity
  it would report 5,277 findings on BO3's own scripts, most of them engine-fixed callback signatures.
- `UnusedIncludeLint` (5012) / `UnusedUsingLint` — an import contributing nothing. 5012's test is
  MARGINAL, not direct, and that is what stops a Hint manufacturing an Error: a file may include a
  hub purely as a conduit, and judging the directive by what its TARGET declares called that unused,
  offered "Remove", and the removal made 5026 fire. It is measured against what is CERTAINLY kept
  rather than against the other candidates — otherwise two conduits each cover the other and both
  are declared removable, which the bulk "remove all" action would then act on. 46 stock directives
  across CoD4, MW2 and WaW were in that state.
- `UnusedLocalLint` (5008) — a local assigned and never read.
- `UsingNotFoundLint` (5009) — an import naming no file.
- `VoidResultLint` (5019) — keeping the result of a builtin that returns nothing. Only builtins:
  GSC declares no return type, so the same claim about a script function would be a guess.
- `GameShapeDetector` — not a lint but the mismatch check behind it: reads a file's directives to
  judge which family it looks like, and reports when the selected profile disagrees.
- `WorkspaceLints` — the composition point that runs the cross-file rules for a document.

## Resolution/InsertCache.cs

- `InsertCache` — the lexed `#insert` headers, shared across every file that inserts them, plus what
  each header CONTRIBUTES (`IHeaderMacroCache`). Keyed by the RESOLVED absolute path, never the path
  as written: `scripts\shared\shared.gsh` means the mod's copy when a mod file asks and raw's when a
  raw file asks, so keying on the written path would let whichever file asked first decide the
  contents for everyone.
- Validated by last-write time rather than by an invalidation message, because a watcher that drops
  an event leaves a stale header — and a stale header changes what macros expand to with no error to
  trace back. A failed read is not cached. See `PERF.md` for what the two caches were worth.

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
  The other games' `<prefix>_api_gsc.json` sit beside them. `waw_api_csc.json` and
  `bo1_api_csc.json` are DERIVED from those games' server libraries by `tools/field-data`
  (pruned to names with evidence, corrected for the leading `localClientNum`) because
  neither game documents its client VM — see GAME_PROFILES.md. Only BO3's is a real source.
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
- `DevOnlyBuiltins.cs` — the conservative fallback set for development-only engine functions;
  API entries can override it when the data carries an explicit `devOnly` value.
- `MacroExpansionPreview.cs` — renders a readable, length-limited macro body for hover and
  substitutes call-site arguments token-by-token rather than by unsafe text replacement.
- `StockScripts.cs` — loads the profile's raw-relative stock-script list and canonicalizes slash
  style and casing for the raw-file warning setting.
