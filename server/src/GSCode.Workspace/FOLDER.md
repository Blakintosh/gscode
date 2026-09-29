# GSCode.Workspace

Workspace layer: the script database (separate GSC/CSC stores), path/mod-overlay
resolution, background indexing, the SQLite cache, and the bundled game data. LSP-free.

Everything below is built. The folders map to the layers: `Resolution/` turns a script path into a
file, `Documents/` holds open-buffer state, `Database/` and `Indexing/` own the record store,
`Cache/` persists it to SQLite between sessions, `Api/` the bundled game data, `Analysis/` the
lints, `Completion/` and `Typing/` the information surfaces.

## Database/ScriptRecord.cs

- `MacroRecord(Name, IsFunctionLike, Parameters, NameRange, Documentation)` — a macro
  surfaced from a file (exact-case name; bodies stay parser-side).
- `DependencyEdge(RawPath, ResolvedPath, IsInsert, Range)` — one #using/#insert edge.
- `sealed record ScriptRecord` — the complete immutable knowledge about one file:
  path (the database key), language, ContextId ("raw"/"mod:x"/"workspace:f"),
  RelativePath (the overlay-shadowing identity), content hash, namespaces, functions,
  classes, macros, dependencies, references, field bindings, diagnostics, IsDirty (unsaved editor
  state, never persisted). Closed files keep ONLY this record.
- `FieldBindings` is lifted out of the parse for the same reason `PathCallTargets` is: a record
  keeps no ParseResult, and the question is cross-file. A callback is bound in one script and
  invoked in another, so answering "what runs when this field is called" by re-parsing the binding
  file would mean parsing unopened files on a request path.

## Database/MethodResolution.cs - members

- `MembersOf` is every `var` reachable on a class, own and inherited, most derived winning. It
  feeds completion's statement scope AND, since a bare name in a class body IS the member,
  navigation's resolution of one.
- `FindDeclaringClassForMember` is the member counterpart of `FindDeclaringClass`, needed for the
  same reason: extraction keys a bare use by the class whose BODY it sits in, which for an
  inherited member is not the class holding the `var`.
- `FindMemberReferences` canonicalizes DOWN to the declarer, then unions back UP across every
  descendant. Both directions are needed - the declaration is on one class and the uses are keyed by
  whichever class's body each sits in - and without the second, renaming a base's `var` would
  rewrite the declaration and leave the subclasses spelling the old name.
- A use in a class whose ancestors are NOT all in the file is indexed too, on the strength of the
  class having an ancestor the parse cannot see: extraction records every bare name there as a
  member of that class. A genuine local recorded that way is inert, because the key carries its own
  name and can only be matched by a query for a member OF THAT NAME - and if the hierarchy really
  declares one, the bare name IS it. This is what makes the reverse direction complete, so renaming
  a base's `var` reaches the subclasses in other files. Cost on bo3: 3,242 member references,
  1.39% of the 232,703 total, across 12 of 980 files.

## Database/ClassGraph.cs

- `ClassGraph` — the per-language reverse index of class declarations, parent links, and method
  names. Replaces repeated workspace-wide scans with path-valued buckets that can be updated or
  removed exactly when one file changes.
- `Apply(path, classes)` is the only entry point, and a REMOVAL is `Apply(path, [])` — the graph
  reads the previous contribution itself, so an empty one empties exactly the buckets that file
  touched. There is no `Remove`: a second entry point would be one more place for the buckets to be
  emptied differently.
- Class declarers are bucketed twice, by bare name and by `(namespace, name)`, in the same `Apply`.
  `LookupClasses` reads the qualified bucket whenever it is given a namespace: in a workspace of
  per-copy namespaces every copy of a class-declaring file shares the bare-name bucket, and a
  qualified lookup read all of them to keep one.

## Database/ExportSignature.cs

- `static ExportSignature` — hashes only the cross-file surface a record exposes: namespaces,
  functions, classes, methods, parameters, and visibility/dev-only flags. Body edits therefore do
  not fan out diagnostics, while changes another file can observe invalidate dependents.

## Database/LanguageStore.cs

- `sealed class LanguageStore` — ONE language world: path-keyed record map + the indexes that
  answer its questions without walking it — `ReferenceIndex`, `DeclarationIndex`, `NamespaceIndex`,
  `ClassGraph`, `RelativePathIndex`, `DependentsIndex`, `DirectiveIndex`, `PathTreeIndex`,
  `VocabularyIndex` — and the overlay counts
  behind `HasOverlayAt`. Upsert swaps records atomically and diffs every index; GSC/CSC isolation is
  two instances of this class, never a filter. Every index key set is built OUTSIDE the write gate,
  from the incoming record alone; only the previous record's is built inside.
- The index list is written out ONCE, in the private `ApplyIndexes(path, previous, next,
  contributions)`. `Upsert` and `Remove` both call it — a removal is an upsert whose contribution is
  `Contributions.None` and whose next record is null, which is what each index's own diff already
  means by an empty new set. One list, so an index added later cannot be updated on upsert and
  forgotten on removal, which would leave a deleted file's keys in it silently.
- `Contributions` is the private holder for what one record contributes to all nine, built by
  `Contributions.Of(record)` outside the gate — the shape that keeps the per-file hashing off it.
- `VisibleDeclaredNames(prefix, contextId)` answers the auto-import producer: the declared function
  names beginning with a prefix, in files that context can see. It reads the DECLARATION INDEX's
  keys — which are the distinct lowercase names — so its cost follows how many names share a prefix
  rather than how many files exist, the same shape `VisibleLiterals` has. Only top-level functions
  are in that index (`DeclarationIndex.KeysOf`), which is the right set: a method is reached through
  an instance, never through an import.
- The rule the indexes exist for: nothing a keystroke or a request pays may walk `AllRecords`. At
  50,000 files each walk that did became a per-request cost growing with the workspace — completion,
  one file's lint pass, CodeLens (PERF.md, the scale section). A new query that needs "every record
  that …" wants an index here, maintained in the same diff, not a loop.
- Queries answered here: `FilesDeclaring(name)` and `FilesDeclaring(namespace, name)`,
  `FilesDeclaringInto(namespace)`, `FilesReferencing(key)`, `FilesAt(path)`, `FilesNaming(path)`,
  `FilesWriting(writtenKey)`, `FilesInserting(headerPath)`, `PathChildren(folder, context)`,
  `MayBeDevOnly(name)`, `VisibleLiterals(kind, context)`, `VisibleFieldNames(owner, context)`,
  `HasOverlayAt(path, context)`.
- The write gates are STRIPED by path, 64 of them, not one for the store. The race they stop is
  between two writers of the SAME file — read-previous and swap are separate steps — and two writers
  of different files share nothing here, because each index below serialises its own dictionary. One
  gate for the store would serialise every index diff against every other one; it made
  `commit.upsert` 28.6% of CoD4's cold-index thread-time. See `PERF.md`.
- `SetDiagnostics(path, expectedContentHash, diagnostics)` — for the `workspaceIndexingMode: full`
  lint sweep: swaps a record's diagnostics in place under the SAME per-path gate `Upsert` uses, but
  touches none of the four indexes, since a lint result changes what a record REPORTS, never what
  it declares or references. Gated on the content hash of what was just read and linted, not a
  hash captured before that read — disk (and the record, via a concurrent watched-file update) can
  move in between, and applying a stale sweep's result onto a record that has already moved on
  would silently regress it back to describing text that no longer exists.

## Database/PackedInvertedIndex.cs

- `internal sealed class PackedInvertedIndex<TKey>` — key→files storage plus the per-file diff,
  shared by every key→files index here (`ReferenceIndex`, the three halves of `DeclarationIndex`,
  `RelativePathIndex`, `DependentsIndex`, both halves of `DirectiveIndex`, both halves of
  `VocabularyIndex`). It started as two
  classes that were the same class twice — same packing, same remove-then-add diff, same snapshot
  read — and had already drifted.
- `FilesFor(key)` is the files behind one key; `CollectKeys(keyFilter, fileFilter, into)` is the
  other direction, the keys at least one accepted file carries — a vocabulary, read one shard at a
  time. Its file filter runs under the shard gate, so it must take no lock of its own.
  `ForEachKey` is the same walk handing each key to a callback with how many files carry it — every
  file, not only accepted ones, since it ranks a list and counting only visible files would test every
  file of every key. A callback so a caller keeping the best few of thousands never holds them all.
- Packed: a bare `string` while exactly one file carries a key, promoted to a `HashSet<string>` only
  once a second appears. Most keys are carried by one file and a HashSet holding one reference costs
  ~150 bytes to carry 8 — on BO1 that is the declaration index costing 5.1 MB against well under
  one. The union never escapes the class.
- Sharded 64 ways by key hash, each shard its own dictionary and lock, taking a shard lock per key
  rather than one lock per diff. Sound only because no invariant spans two keys. This took
  `commit.upsert` from 28.6% of CoD4's cold-index thread-time to 8.4%; see `PERF.md`.
- `NamespaceIndex` is deliberately NOT built on this — see its entry.

## Database/ReferenceIndex.cs

- `sealed class ReferenceIndex` — the inverted key→files index. `KeysOf` turns a record's
  reference list into keys outside the caller's write gate; the storage and the diff come from
  `PackedInvertedIndex<SymbolKey>`. Exact ranges come from scanning the named files' reference lists.
  `KeysNamed(name)` answers the one question that is about a NAME rather than a key — an engine
  builtin, whose call sites are keyed by whatever scope each was written in — by reading the index's
  KEY SET, so it stays bounded by how many distinct scopes mention the name rather than by how many
  files exist. Same shape `DeclarationIndex.NamesStartingWith` uses for completion.

## Database/DeclarationIndex.cs

- `sealed class DeclarationIndex` — the name→declaring-files index, the counterpart to
  `ReferenceIndex`, and literally so — both are wrappers over `PackedInvertedIndex<TKey>`. Holds
  PATHS rather than records for the same reason: a record is swapped wholesale on every edit,
  so holding one would pin a stale version. Keyed on `FunctionSymbol.KeyName` and compared ordinally
  — exactly the comparison `LookupFunctions` performs, so the candidate set is identical.
- It saves `LookupFunctions` a walk of every record and every function (~30,000 symbols on BO3) per
  CALL SITE, which made four lints 97% of the cross-file lint cost. It narrows WHERE to look and
  decides nothing: visibility, namespace, privacy and overlay shadowing all still apply after it.
  See `PERF.md`.
- Kept three ways, all from one `KeysOf(record)`: by bare name; by `(namespace, name)`, which a
  namespaced lookup reads instead of the bare list (every bo3 system file declares `__init__`, so
  `util::__init__` would otherwise walk all of them); and the names with a DEV-ONLY declaration
  among the file's functions and class methods, behind `MayBeDevOnly` — case-insensitive, so it can
  only answer "maybe" too often, which is the safe direction for the prefilter `DevBlockCallLint`
  uses.

## Database/NamespaceIndex.cs

- `sealed class NamespaceIndex` — the namespace→declaring-files index, the third of the inverted
  indexes and built for the same reason as the other two: a lookup instead of a walk of the whole
  store. `FilesDeclaringInto` narrows the candidate set for a namespace before anything reads a
  record.
- Holds a plain `HashSet<string>` per namespace rather than sharing `PackedInvertedIndex<TKey>`
  with the other two, because a namespace is declared into by many files by nature, where a function
  name usually is not: the packing saves nothing here and the diff it needs is genuinely simpler.
  Unifying all three would take a flag to tell the two shapes apart, which is the sign they are not
  one shape.

## Database/CallResolution.cs

- `static CallResolution` — which function a WRITTEN call names, shared by signature help and the
  parameter-name inlay hints. `EnclosingClassAt`, `UnqualifiedFunction` (the
  `ResolvesByNamespace` split: `FunctionInIncludeScope` on the merge dialects, the declared-namespace
  loop on BO3) and `PathQualifiedFunction` (`maps\_utility::name`, scoped to the file the path names
  by asking with an empty asking path). Builtins are NOT consulted here: the two callers present an
  engine function differently enough that each keeps its own fallback.
- It exists because the two had a resolver each and one of them never got the merge-dialect rule, so
  inlay hints answered only for the asking file's own functions on four of the five games. What stays
  separate is what genuinely differs: signature help is token-driven and works before there is a
  tree, the hints walk the tree, and each builds a different answer from the same symbol.
- One disagreement is left and documented at all three sites rather than settled by refactor:
  `Foo::bar()` where `Foo` is both a namespace and a class resolves class-first in signature help and
  namespace-first in the hints. BO3 ships three such names.

## Database/FunctionLookupCache.cs

- `sealed class FunctionLookupCache` — a memo over `LookupFunctions` for the span of ONE file's
  analysis. The lookup's `limit` is part of the memo key, so a capped answer is never served to a
  caller that asked for the whole one. Scripts call the same handful of names repeatedly, so the same question was asked dozens
  of times per file. Per file and discarded with it, deliberately: a longer-lived cache would need
  invalidating on every edit anywhere in the workspace, since an unqualified call under a merge
  dialect resolves by name across everything indexed — a subscription problem, not a dictionary.

## Database/RelativePathIndex.cs

- `sealed class RelativePathIndex` — script-relative path (`Normalize`: lowercase, backslashed, no
  extension — the form `#using`/`#include` write) → the files AT that path; more than one when an
  overlay shadows a raw file, so callers still apply visibility and shadowing. Replaced the store
  walks that normalized every record's path per request to find the handful an import list names
  (`ImportedNamespaces`, `FunctionsInIncludeScope`, inline `path::` completion), reached through
  `DatabaseQueries.RecordsAt`.
- `Normalize` is THE spelling of that form, not one of several. Everything that compares a written
  path against this index folds it here — the reachability queries look their answer up in
  `FilesAt`/`FilesNaming`, which are keyed on it, so a second spelling that drifted would match
  nothing and report an empty result rather than an error.

## Database/DependentsIndex.cs

- `sealed class DependentsIndex` — script path → the files that NAME it through a non-insert import
  edge or an inline path call: the reverse of the dependency graph, in `RelativePathIndex`'s form.
  It is what `FindReferencesReaching` reads: scoping keeps a reference only when its file is the
  declaring file or names it, so those files are all that can contribute.

## Database/DirectiveIndex.cs

- `sealed class DirectiveIndex` — every record's directives, reversed two ways: by the path as
  written (`WrittenKey`) and, for insert edges, by the header the edge resolved to. It is what
  `DatabaseQueries.ScriptsInserting` (a changed header's dependents, through header-to-header
  inserts) and `DependencyRewrite.PlanRename` read, instead of testing every record's edges.
- `WrittenKey` folds separators, case, surrounding whitespace and leading backslashes, which makes
  it LOOSER than either caller's comparison — the watcher's `NormalizeScriptPath` equality and the
  planner's canonical form. The index only narrows where to look; both callers still run their own
  test on what it returns. Not `DependentsIndex`, which leaves insert edges out on purpose.
- Kept by each `LanguageStore` and by the header store in `ScriptDatabase`.

## Database/PathTreeIndex.cs

- `sealed class PathTreeIndex` — for each script-relative folder, the segments directly under it and
  whether each is a folder or a file, counted PER CONTEXT: what a folder lists depends on who asks,
  and a segment is listed when any context the asker can see holds a file under it. Path completion
  inside `#using`/`#include`/`#insert` reads it instead of rewriting every record's path per
  keystroke. Folders and segments compare ignoring case; the spelling kept for a segment is the
  first one indexed.
- Built with `keepExtension: false` in each language store (the form `#using` names a script in)
  and `true` in the header store (`#insert` writes the `.gsh`).

## Database/VocabularyIndex.cs

- `sealed class VocabularyIndex` — the workspace's distinct literals (by `SymbolKey`) and assigned
  fields (by name, on any owner), each → the files using it, for literal and field completion.
  Occurrences grow with the workspace and distinct names barely do, so both lists read this rather
  than every reference / assignment of every record per request. A literal is indexed only when
  literal completion could offer it (a plain `ReferenceKind.Literal`, not from a macro body); the
  name-shape filter stays at the query. Read through `LanguageStore.VisibleLiterals` /
  `VisibleFieldNames`, as `VocabularyName(Name, Files)`: each name with how many files write it
  (literals handed to a callback, fields as a list).
  Field names are kept as WRITTEN — `level.foo` and `level.Foo` are two names here — and choosing a
  spelling is the completion list's call. Not keyed by owner: the name before the dot is a variable,
  not the object (`self` is whatever a function was called on; after `blah = level;`, `blah.` is
  level), so scoping a field list by it hid fields the object has.

## Database/ScriptDatabase.cs

- `sealed class ScriptDatabase` — the façade: `Gsc`/`Csc` stores + the shared GSH
  record map (headers serve both worlds). The header store keeps its own `DirectiveIndex`,
  `PathTreeIndex` and `ReferenceIndex`, diffed in the private `ApplyGshIndexes` that both
  `UpsertGsh` and `RemoveGsh` call, under ONE gate — headers
  are a few hundred files, so the per-path striping the script stores need buys nothing — and read
  through `GshFilesWriting`, `GshFilesInserting`, `GshPathChildren` and `GshFilesReferencing`. `Commit` builds and stores a record from a
  ParseResult, through `CommitRecord` — the one place a record is routed to the store that owns it,
  headers to the shared GSH store and everything else to its language world. `BuildRecord` is the
  pure builder (macros filtered to file-local,
  dependency edges from inserts + usings, xxHash64 content hash). `CanSee` encodes the
  visibility rule (raw←raw; mod M←{M,raw}; workspace←{workspaces,raw}); `ContextIdOf`
  stringifies contexts. `SetDiagnostics(path, language, expectedContentHash, diagnostics)`
  delegates to the language's store — see `LanguageStore.SetDiagnostics`.
- `HasCompletedIndex` / `MarkIndexComplete()` and `HasCompletedLintSweep` /
  `MarkLintSweepComplete()` — two separate volatile-backed flags, not one. The first gates the
  four lints that need the index to answer "does this function exist" at all
  (5013/5014/5025/5026); the second gates whether a CLOSED file's stored diagnostics include the
  cross-file lints yet (`workspaceIndexingMode: full`'s sweep) — a narrower, later question the
  first flag cannot answer.

## Completion/CompletionEntry.cs

- `CompletionKind` + `CompletionEntry` — the LSP-free completion suggestion model. `ImportPath`
  names the script an entry's symbol lives in when accepting it has to add an import first, and is
  "" for every entry already in scope. A PATH rather than an edit: where a directive goes is a
  question about the document, which the handler answers. `Narrowed` marks a row from a list cut to
  the text typed so far (literal and field completion), which the handler turns into `isIncomplete`.

## Completion/GscKeywords.cs

- `static GscKeywords` — the statement-scope and top-level keyword/directive lists offered in
  completion (assert/assertmsg excluded — they come from the builtin API instead). Documented
  entries get their KeywordDocs blurb as the completion's documentation. A body is offered
  `StatementKeywords`; file scope is offered BOTH lists, since a top-level macro invocation opens
  an expression there — see the engine below.
- `BodyDirectives` is the third list: the directives the preprocessor dispatches from its flat
  token walk (`#if`/`#elif`/`#else`/`#endif`, `#define`, `#insert`), which are therefore legal
  inside a function body as well as at file scope. Everything else in the family is consumed by
  the parser at file scope and stays out of the in-body list.

## Completion/CompletionEngine.cs (+ .Context / .Producers / .Entries partials)

- `sealed partial class CompletionEngine.Complete(result, contextId, position, includeLiterals)` —
  context-aware completion driven by the tokens around the cursor: inside a
  `"..."`/`&"..."`/`#"..."` literal it offers the known literals of that kind from the store's
  `VocabularyIndex` (gated by `includeLiterals` = the completion.literals setting; disabled →
  nothing, since statement scope makes no sense in a string); otherwise `#precache(` asset types,
  `#using`/`#insert` path segments (from the stores' `PathTreeIndex`), `ns::` (that namespace's functions only), `owner.` fields
  assigned on ANY owner
  (+ `.size`; also from `VocabularyIndex` — one row per field ignoring case, labelled in this file's
  own spelling or else the most-used one; the detail reads `level.foo · field`, and a field written
  in more than one casing adds a dimmed `+N spellings` and lists them, with file counts, in the
  documentation — `FieldEntry`), and statement scope (keywords, the dialect's global objects and snippets,
  the enclosing function's parameters and locals, every macro in scope, namespace functions,
  visible classes, namespace-less builtins as call snippets).
- **Literal and field lists are cut to what has been typed.** The file's own literals / fields and
  `.size` always; of the workspace's vocabulary (and the engine fields and map keys), the best 200
  containing the typed text, ignoring case — names it begins first, then by how many files write
  them — kept by `VocabularyCut`, a bounded heap fed straight from the index walk. Every row is
  `Narrowed`. Sending cod4's 18,144 literals whole was 2.3 million characters of JSON per request,
  70 ms to serialize against 7 ms to build.
- **File scope gets that same list.** None of the store's categories is a per-cursor fact — the
  macro table is per FILE and a function is in scope for the file — and `REGISTER_SYSTEM` alone is
  written at column 0 in 477 of the shipped BO3 scripts. File scope is also not declarations-only,
  which is why narrowing the list was not the fix instead: a top-level macro invocation is a CALL,
  so it opens an expression there, and BO3 writes 510 function pointers and 467 `undefined`s inside
  those argument lists. Hence both keyword lists at file scope. What stays behind is what is not
  BOUND there: parameters and locals (nothing is bound outside a declaration) and the engine globals
  (`self` at file scope is unwritable, and a global sorts in the first tier, so it would head every
  list).
- Two punctuation rules are decided once above the dispatch, since each is a fact about the POSITION
  rather than about the list an arm produces. **File scope takes no terminator** —
  `IsStatementPosition` finds the previous function's `}` and answers true, which is right in a body
  and meaningless where there are no statements; all 447 `REGISTER_SYSTEM` uses carry no semicolon.
  **A function pointer takes no parentheses at all** — `&foo` names the function where `&foo()`
  calls it, and BO3 writes 4,564 pointers against four `&name(` of any kind. The pointer rule covers
  `&ns::foo` too (585 of them), and is gated on `GameProfile.FunctionPointerStyle`, since pre-BO3 a
  pointer is a bare qualified name and an `&` is arithmetic. Only the suffix changes: what may be
  pointed at is what may be called, so the producers already answer that position.
- `CollectLocalScope(function, position, entries)` — the names bound INSIDE the function being
  edited, and the only per-function list here. Parameters, then the locals introduced by an
  assignment AT OR ABOVE the cursor; an owner (`self.count = 1`) makes it a field rather than a
  local, the same exclusion `LocalDefinition` makes. Below the cursor is left out because nothing is
  bound there yet and reading it earns a 5016 — the rule `vararg` is held to. Without them the
  server's lists (a median of ~1,900 entries) out-score the editor's own word-based suggestions that
  would otherwise cover them.
- Macros come from `Preprocessed.Macros.All` WHOLE. That table is built per parse from the root file
  plus the headers it `#insert`s, so it already is "what this file can expand"; filtering it to
  `SourceFile is null` would keep the root file's own and drop every constant a shared `.gsh` exists
  to supply. A function-like macro takes the call punctuation a function does, since at the use site
  it is a call; the detail names the defining header. Gated on `GameProfile.HasMacros`, like every
  other category here is gated on the dialect: the preprocessor records a `#define` whatever game is
  active, but only BO3 has a preprocessor, and in the IW line the one `#define` per corpus is a
  commented-out block of C in `_hud.gsc`.
- A `#` INSIDE a body has two readings and gets both: `GscKeywords.BodyDirectives`, plus the
  `#"..."` literals where `GameProfile.HasHashStrings`. Read as a hash string alone it would lose
  the `#if` family everywhere and give the three dialects without hash strings an empty list.
- An inline path (`maps\mp\_utility::foo`) is detected on `\` ONLY. `/` is division, and accepting
  it would classify `hp = maxhealth/2` as a path; the directives' own scan still normalises both,
  where there is no such collision.
- Split across four files because as one class it hides the shape of the thing: `Complete` is a
  dispatcher over ~10 contexts, and reading which context reaches which query meant scrolling past
  every scanning helper in between. Same convention as `Parser.cs (+ .Declarations / .Statements /
  .Expressions)`, and the same reason.
  - `.Context.cs` — WHERE the cursor is. All static, and reads only tokens, source text and the
    parse tree: `IsStatementPosition`, `FindLiteralAtOffset`, `EnclosingFunction`,
    `PreviousSignificant`, `TryPrecacheContext`, `IsAddressOfPosition` and the rest. `EnclosingFunction` (which delegates to
    Core's `EnclosingFunction.At`, the walk that knows methods live on their class) is carried by the
    dispatcher as a symbol rather than reduced to a bool, since the same walk answers which keyword
    set is legal, whether `vararg` binds, and which parameters and locals are in scope. Nothing here
    touches the database, which is the property that makes the boundary hold.
  - `.Producers.cs` — the lists themselves, one producer per context. This is the file that reaches
    `ScriptDatabase`, `BuiltinApiSet` and `ObjectFields`, so its cost is a function of the
    workspace rather than of the file; PERF.md's completion sweep measures it.
  - `.Entries.cs` — one symbol → one `CompletionEntry`: labels, detail text, call snippets,
    parameter hints. Shared by several producers and owned by none.

## Completion/GscSnippets.cs

- The snippets whose construct only SOME dialects have — `foreach`, `class`, `new`, the BO3
  function modifiers, every import directive, `#precache`, and the two ScriptDoc forms. They cannot
  be contributed by the extension: a contributed snippet is registered per language id, one id
  covers five games, and VS Code merges them in unconditionally with no way to withdraw one. That is
  how CoD4 came to be offered a `foreach` loop it cannot run.
- Each entry is gated on a keyword or directive passed to `GscKeywords.IsAvailable`, so a snippet
  and the word it writes cannot disagree about which games have it. The ScriptDoc pair is the
  exception, gated on `ScriptDocStyle` because neither form is a word.
- The pre-BO3 ScriptDoc snippet writes the `///ScriptDocBegin`/`///ScriptDocEnd` fence and quotes
  its lines. It did neither for a long time, and what it inserted therefore read back as an ordinary
  comment — `SymbolExtractor.IsDocCommentToken` requires the fence there, since a pre-BO3 doc block
  has no delimiter of its own — so the function it documented hovered bare. `ScriptDocTemplate`
  renders the same shape for the code action.
- `Entry.Retrigger` reopens the suggestion list after a snippet is accepted, and only `precache`
  sets it. Its first argument is an asset type, the list of those is per-world, and carrying the
  names in the body would be a second copy of `PrecacheAssetTypes` with the `.gsc`/`.csc` split
  applied to it twice — so the body leaves a tab stop inside the quotes and `AssetTypeCompletions`
  answers, which is the same arm a typed `#` retriggers into.
- The UNIVERSAL snippets stay in `client/snippets/common.json`, where they cost nothing and work
  before the server has started. The function declaration is neither: `FunctionDeclarationSnippet`
  builds it per dialect, since the merge games declare with a bare name.

## Completion/SignatureEngine.cs

- `SignatureParameter`/`SignatureResult` + `SignatureEngine.Resolve(...)` — scans back from
  the cursor to the enclosing '(', identifies the callee (macro / script function / builtin) and
  the active parameter (top-level comma count), and renders the signature + parameter docs.
- The MACRO is asked first, and from `result.Preprocessed.Macros` rather than the store: the
  preprocessor substitutes before the parser runs, so where a `#define` and a function share a name
  the function's parameters describe code that never executes. The lookup is ORDINAL — macro names
  are the language's one case-sensitive kind — and an object-like macro answers null, since
  `MAX_PLAYERS( x )` is a body followed by a parenthesised expression rather than a call. Unlike
  macro COMPLETION this is not gated on `HasMacros`: completion decides what to propose, this
  describes a name already written, and the preprocessor expands a `#define` on every dialect.

## Database/SymbolAtPosition.cs

- `HitKind` + `PositionHit` + `static SymbolAtPosition.Resolve` — the one resolver behind
  hover/definition/references/highlight/documentLink: finds the classified reference
  (function/class/macro/field/literal) or #using/#insert dependency path at a position,
  working from either a stored ScriptRecord or an open document's live ParseResult. Both overloads
  read the reference list through one private `ResolveReference`, so the macro guard — never resolve
  a cursor to text a macro expanded into — has one place to change rather than two.

## Database/LocalDefinition.cs

- `static LocalDefinition` — resolves a local variable or parameter to its introducing declaration
  within the enclosing function. Locals stay outside the shared reference index, so this AST-based
  lookup prevents same-named variables in unrelated functions from colliding.

## Database/LocalReferences.cs

- `readonly record struct LocalOccurrence(Range, IsWrite, IsDeclaration)` + `static LocalReferences`
  — the occurrence list for a local, the companion to `LocalDefinition` and outside the shared index
  for the same reason. Backs find-references, document highlight and rename on variables.
- `Find` walks the enclosing function's body through `LocalUses.Of` (Parser), the one read/write
  classification it shares with `UnassignedVariableLint` and `UnusedLocalLint`, so what rename
  touches and what the lints report can never disagree about what a use of a local is.
- `IsDeclaration` marks the parameter, or the first write when there is none — the same "where the
  name is introduced" rule `UnusedLocalLint` reports against. Only that one is dropped when a
  request excludes the declaration; a later `x = 2` is a reference to something already existing.
- Answers EMPTY rather than a wrong per-function answer when the name is not the function's to own:
  a global from the profile, an Infinity Ward file-scope constant (readable from every function), a
  class `var` member or an ancestor's (same file), a macro-expanded token, `vararg`/`thisthread`.
- `BindsName` is the collision test a rename needs. Renaming onto a name the function already binds
  does not fail — it MERGES two variables and the script keeps running meaning something else. The
  same refusal covers names arriving from OUTSIDE the function (a global object, an IW file-scope
  constant, a class member): renaming a local onto one captures every read that reached the outer
  name.
- `SemanticTokens` classifies every parameter and local in a file for highlighting — the two legend
  slots `SemanticTokenBuilder` cannot fill, produced from the same walk and the same exclusions so
  what is coloured as a local and what rename/references answer on cannot drift apart. A name
  nothing binds stays uncoloured: it is undefined, which is the unassigned-variable lint's to say.
- Finds the name token itself rather than calling `AstSearch.TryFindLocalContext`, which reports an
  `IdentifierNode`: a parameter, a `foreach` key/value and a `const` name are bare tokens on their
  declaring node, so clicking the binding — the occurrence a user is most likely to click — found
  nothing at all.

## Database/DatabaseQueries.cs

- `ResolvedFunction`/`ResolvedClass` + `static DatabaseQueries` — context-filtered lookups that
  MERGE namespaces across contributing files and apply overlay shadowing (same RelativePath: overlay
  beats raw); `FindReferences` returns visible (record, entry) pairs for a key. Private functions
  follow NAMESPACE privacy, not file privacy — a namespace can be split across files, so any file
  declaring it may call in; callers pass their namespaces via `askingNamespaces`
  (`result.Extraction.DeclaredNamespaces`, from the live parse so unsaved edits count), and
  one that cannot falls back to same-file visibility. `FindGshReferences` is the deliberate
  language-guard exception: a `.gsh` serves both languages, so macros declared in headers live in
  the shared GSH store and are unreachable from either LanguageStore; it reads the header store's
  reference index. `ScriptsInserting(header)` is the GSC/CSC records a header's analysis decides —
  inserting it directly or through other headers, or waiting on its path unresolved — read from the
  `DirectiveIndex`es. `FindAllReferences` extends that exception to the language stores themselves —
  a MACRO key is answered against BOTH worlds whoever asks, because a header is inserted into `.gsc`
  and `.csc` alike and the asking file's language decides nothing about where its uses are. Scoped
  to the asking store, a rename started in a `.gsc` would leave every `.csc` use spelled the old
  way. Functions keep the isolation: a same-named function in the other world is a different
  function.
- `LookupFunctions` asks `DeclarationIndex` for its candidate files rather than scanning every
  record — the `(namespace, name)` list when a namespace is given. It applies overlay shadowing PER
  RECORD inside the walk (a raw record is skipped when `HasOverlayAt` its path — `ApplyShadowing`'s
  rule with a store, whose other half is implied), which is what lets `limit` stop the walk early: a
  caller asking whether a name resolves passes 1, one asking whether it resolves to exactly one
  passes 2. A bare name on a merge dialect has thousands of declarations at scale, and building all
  of them to answer "yes" was two lints' whole cost. `BoundedLookupTests` keeps the two-pass rule as
  a reference implementation. `IncludeClosure` walks the `#include` graph TRANSITIVELY — the
  compiler flattens the chain, which the corpus settled — and reports whether the walk saw
  everything, since a rule may only assert a name is out of scope against a complete one. The
  direct-only helpers beside it (`FunctionsInIncludeScope` and friends) stay narrow on purpose:
  completion offering too little is harmless where an Error is not. `FunctionInIncludeScope` is that
  scope's answer for ONE name, for signature help, which asks per keystroke inside an argument list
  and would otherwise build every function the scope offers to read one. It decides shadowing per
  record rather than over the set, which is sound HERE and not in `ApplyShadowing`'s general case
  because the scope has already dropped every record the asker cannot see — what is left of the set
  rule is then exactly what `HasOverlayAt` answers alone. `IncludeScopeLookupTests` keeps the full
  list as the reference implementation.
- `LinkedScriptPaths(result, profile)` — the paths a file links against in whichever directive its
  dialect uses (`#using` where namespace-driven, `#include` where merging), and the ONE place that
  fork lives. Scoping callers want this rather than `ImportedScriptPaths`/`IncludedScriptPaths`,
  which stay public for the cases that genuinely mean one directive: asking for the wrong one returns
  an EMPTY array rather than throwing, and an empty scope reads downstream as "nothing matched" and
  silently falls back to the unnarrowed set.
- `FindReferencesReaching(stores, context, key, declaringRelativePath)` — exactly
  `ScopeToIncludeGraph(FindAllReferences(...), declaring)` for a function key, read from the files at
  the declaring path plus the files naming it (`DependentsIndex`) instead of every file mentioning
  the key, walking whichever of that set and `FilesReferencing(key)` is shorter. Shadowing is applied
  as `FindAllReferences` applies it, BEFORE scoping: a raw file drops when a visible overlay at its
  exact path references the key at all. On cod4 at 50K this took a whole-file CodeLens from 707 ms
  to 3 ms; `ReferenceScopeCorpusTests` proves it identical over every stock declaration.
  Both it and `FindAllReferences` take an `onlyPath`, for a question that is same-file by definition
  (document highlight): the file list is still built, since it is what decides whether that file
  contributes, but no other file's reference list is READ. `FindAllReferences` swaps `ApplyShadowing`
  for the per-record test this one already makes when narrowed — the same rule, not a near one, since
  every record `FilesReferencing` returns references the key by construction.
  `SameFileReferenceTests` pins the overlay cases stock corpora have none of, and
  `ReferenceScopeCorpusTests` proves the narrowed answer equals the filtered wide one over all 28,809
  declarations of a real bo3 and cod4 index.
- `PreferIncludeScope` and `ScopeToIncludeGraph` narrow definitions and references to what the
  asking file can reach, on either dialect family. A namespace does not pin a file — the `mp` and
  `zm` copies of a script share one `#namespace` — so a namespace-driven key still names several
  declarations and still has to be narrowed by the `#using` graph. `CanReach` answers both families
  unchanged, a `#using` edge being a non-insert dependency edge exactly as an `#include` is.
  Attribution matches on the whole KEY via `GameProfile.KeyNamespace`, so the "declares it itself"
  shortcut cannot claim a same-named function from an unrelated namespace.

## Database/MethodResolution.cs

- `ClassMethod` / `static MethodResolution` — canonicalizes class-method call keys across bare,
  qualified, inherited, and unknown-receiver arrow calls. It supplies the shared method surface
  for completion/signature help/hover and the reference union used by definitions and code lenses.
- Every ancestor walk goes through the private `WalkAncestors` — most derived first, `MaxDepth`
  bounded, visited-set guarded, a local class beating the store's copy of the same name.
  `FindDeclaringClass`, `MethodsOf` and `MembersOf` had each written that walk out, so a change to
  any of those rules had to land in three places for the three answers to keep agreeing.

## Indexing/WorkspaceIndexer.cs

- `IndexingMode` (Off/Partial/Full) + `IIndexProgressListener` (+Null impl) —
  the server maps listener events onto gscode/indexing* notifications.
- `sealed class WorkspaceIndexer` — cold start: enumerate targets → bounded
  `Parallel.ForEachAsync` (cores−1) running the per-file pipeline → Commit records.
  Reads the current resolver via an injected `Func<PathResolver>` so resolver swaps take
  effect immediately. Takes an optional `GameProfile`, null meaning `GameProfile.Active` at
  analysis time — which is what the server wants, selecting the game once at startup, and what
  a caller holding a profile must override: indexing under the wrong dialect does not fail, it
  parses declarations away and leaves the store EMPTY. Inserts go through the shared `InsertCache`
  and a `ResolverInsertProvider`, the same two the document path uses, so each GSH is lexed once no
  matter how many scripts insert it; `InvalidateGsh` drops one on change. The argument is optional
  and the field is not — a caller supplying none gets its own cache rather than no cache.
  `IndexFile` is the single-file path the watcher reuses. `UseCache` enables warm-restore:
  the two-pass `IndexAsync` restores files whose on-disk content hash matches the cached
  `CachedEntry`, deserializing the blob only once that check passes and skipping the parse, then
  re-parses restored files that #insert a header which
  itself changed (phase two), and write-throughs every fresh analysis to the cache. Phase
  two closes the changed-header set over the insert graph first, since a restored `.gsh`
  that inserts a changed one contributes something new despite its own bytes matching.
  `RemoveFile` drops a deleted file from the database, the cache, and the GSH lex cache.
  The restore snapshot is held for one pass only: `IndexAsync` releases it in a `finally`, so a
  server-lifetime singleton does not carry 21 MB (bo3) or 64 MB (bo1) of gzipped blobs for the
  session (now binary, ~7 KB a file). `ReloadRestoreSnapshot` re-reads it for the one caller that indexes twice — the
  workspace-folder handler — and swallows a read failure, since a cache closed by
  `gscode/clearCache` should give a cold index rather than an exception.
- `IndexAsync` takes an internal `SemaphoreSlim` (`_passGate`), held for the whole pass: the
  startup pass and a workspace-folder-change re-index can both call `IndexAsync` on the SAME
  instance, and interleaving two passes corrupted `_restored` (one pass's snapshot replaced mid-read
  by the other's reload) and `_skippedOversized` (reset mid-count). `reloadSnapshot: true` runs
  `ReloadRestoreSnapshot` INSIDE the gate rather than as a separate call before it, closing the
  window where a concurrent pass's reload could land in between the two.
- `ownedByEditor` (optional, on `IndexAsync`/`ProcessFile`): an open document's buffer, already
  committed by the text-sync handler, is the source of truth, and this pass has no way to know
  whether that commit landed before or after its own disk read — so for an owned path it builds
  the record (`ScriptDatabase.BuildRecord`, still enqueued to the cache) without calling `Commit`,
  the same "skip the store write, keep everything else" rule `WatchedFileUpdater.Apply` already
  applied to an on-disk change behind the editor's back.
- `AnalyzeForLintSweep(path)` — read + analyse with NO commit, NO cache write, NO header seeding:
  purely a `ParseResult` for the `workspaceIndexingMode: full` lint sweep (`WorkspaceLintSweep`,
  server-side), which needs one because records do not retain theirs. Reads and analyses through
  the same two private steps `ProcessFile` does — `TryReadForAnalysis` (null when unreadable or
  oversized) and `AnalyzeFromDisk` — so the sweep lints exactly what the index would see.

## Cache/CacheSchema.cs

- Format 7 added `ScriptRecord.FieldBindings` AND put `ReferenceKind.FieldWrite` in the middle of
  the enum; format 8 put `FieldUpdate` beside it. Those halves are why neither bump was optional:
  a kind goes on the wire as its ordinal, so an older blob reads every kind after the insertion
  shifted by one - a format-6 blob would read each of its macro uses as a literal.
- `static CacheSchema` — SchemaVersion + RecordFormatVersion (the hand-bumped gates),
  the meta keys, and the `meta`/`files` table DDL. Either version mismatch (or a
  build-identity mismatch) wipes the cache; there are no migrations.

## Cache/ServerBuildIdentity.cs

- `static ServerBuildIdentity.Compute(dataFilePaths, game)` — a SHA-256 fingerprint of the active
  game + the engine assembly MVIDs + the bundled data-file hashes. Any rebuild that could change
  analysis output changes this, invalidating the cache automatically. The game is in the material
  explicitly: a record is dialect-specific, and restoring one game's into another's session is
  undetectable downstream. Left to the bundled data files differing, MW2 (which ships none) would
  share any other data-less game's identity.

## Cache/RecordSerializer.cs

- `static RecordSerializer.Serialize`/`Deserialize` — a ScriptRecord to/from a compact binary
  blob: the record's fields in declaration order with no names, integers as LEB128 varints, strings
  through a per-blob table (written once, then by index), a leading format byte, the body deflated.
  It replaced gzipped JSON when the scale sweep showed restoring a JSON record cost MORE than
  re-parsing the source (bo3: 12.5 s of `index.restore` thread-time against 7.9 s to analyse and
  commit); binary is ~8x cheaper and made the warm start beat the cold one.
- Positional, so a new field is dropped silently unless it is added to BOTH halves in the same
  place and `RecordFormatVersion` is bumped. `RecordSerializerTests` fails when any record type
  gains or loses a settable property, and `RecordFormatCorpusTests` round-trips every record a real
  index produces and compares their JSON. Deserialize returns null on anything malformed — truncated,
  corrupted, or another layout — so one bad row costs one re-analysis.

## Cache/CachedEntry.cs

- `sealed record CachedEntry(ulong ContentHash, byte[] Blob)` + `Materialize()` — one cached
  record still in its stored form. The deserialize is deliberately NOT done when the cache is read:
  `LoadAll` runs before the indexer knows which files are current, so materialising everything there
  paid gzip inflation and a JSON parse for files about to be re-analysed anyway — on ONE thread, in
  front of an index that runs on all of them. That made a warm start slower than having no cache at
  all (bo3 1,509 ms of restore against a 390 ms cold index). Handing the indexer the blob moves both
  halves into its parallel per-file loop, behind the content-hash check. The hash is stored beside
  the blob rather than inside it for exactly that reason. See `PERF.md`.

## Cache/SqliteCache.cs

- `sealed class SqliteCache : IAsyncDisposable` — the per-workspace cache.
  `ResolveDatabasePath` (→ %APPDATA%/gscode/cache/&lt;hash&gt;.db), `CleanUpLegacyCache`
  (deletes the old single-file gzip-JSON cache), `Open` (WAL + busy_timeout, creates
  tables, wipes on version/identity mismatch), `LoadAll` (warm-restore input, as `CachedEntry`
  rows rather than records — see below),
  `Enqueue`/`EnqueueDelete` (never block; `Enqueue` SERIALIZES on the calling thread, so the work
  runs across every indexing core, and hands an unbounded channel the blob — a single background
  writer is left with only the SQL, coalescing batches into transactions; dirty records are skipped
  before paying for it), and
  `DisposeAsync` (drains the writer + checkpoints so a clean exit loses nothing), and
  `WaitForIdleAsync` (returns once every enqueued command has COMMITTED, not merely left the queue).
- The channel is unbounded, so a backlog costs memory (the compressed blobs, until written) rather
  than data: bounded at 4,096 with the writer serializing, it refused 82% of writes at 50,000 files
  and the next "warm" start re-analysed four files in five. `DroppedWrites` counts only writes after
  `DisposeAsync`.
- `WaitForIdleAsync` waits on ONE counter of outstanding commands, incremented before a command
  reaches the channel and decremented only after its transaction commits. The channel's `Count`
  against a writer flag would leave a gap — queue empty, flag not yet set — where a poll reports
  idle with a write in flight (`CacheRowPruningTests` caught that at about one run in seven under
  load). Nothing reads `Count`.

## Indexing/WatchedFileUpdater.cs

- `WatchedFileChange` (Created/Changed/Deleted) + `sealed class WatchedFileUpdater` —
  applies on-disk changes to the database: re-index created/changed files, drop deleted
  ones, and when a GSH changes invalidate its lex cache and re-index every file that
  #inserts it (via `ReindexInserters`, which asks `DatabaseQueries.ScriptsInserting`) so macro
  edits propagate. Returns
  the touched paths for diagnostic republishing. Takes an `ownedByEditor` predicate and
  skips every record it would rewrite for a file that is OPEN — the changed file and any
  dependent alike — because this reads disk and a buffer may hold unsaved edits.
- A create or delete (either language, not just GSH) also calls
  `WorkspaceIndexer.InvalidateResolutionCache()` — a file appearing or vanishing can falsify any
  cached "does this exist" answer `PathResolver.Resolve` has given out, for a `.gsc`/`.csc` target
  as much as a header. A plain content change does not: nothing about whether a target EXISTS
  moved.

## Documents/DocumentStore.cs

- `sealed record AnalysisSnapshot(Result, Version)` — a completed parse and the document version
  its text came from, as ONE value. Two analyses can be in flight on one document (the debounced
  run and a request thread through `AnalyzeIfStale`), so the result and the version it is stamped
  with are published in a single reference write and `Publish` refuses one from an older version
  than the snapshot already there. As separate fields they could interleave into a new version
  stamped on an old parse — a document reporting itself FRESH while holding replaced text.
- `sealed class OpenDocument` — one open editor file: normalized path, language, live
  SourceText, version, the latest `AnalysisSnapshot` (`LatestResult`/`IsStale`
  all read from it), and the pending-analysis CTS (newer edits cancel in-flight debounced runs).
- `sealed class DocumentStore` — open-document tracking keyed by normalized path.
  `Open`/`Close`/`TryGet`, `ApplyChange` (LSP incremental splice or full replace), and
  `Analyze` (runs ScriptAnalysis with an insert provider bound to the file's context
  via the injected factory, and hands back whichever snapshot stands afterwards).
  `Analyze` is `AnalyzeSnapshot(document).Result` — a thin projection kept for the many callers
  that read `document.Version` themselves right after. `AnalyzeSnapshot` is for the one caller
  that cannot (`TextSyncHandler.AnalyzeAndPublish`, publishing diagnostics): it needs the WINNING
  version stamped on what it publishes, not whatever `document.Version` has become by then, since
  those can differ when two analyses of one document race and the version CAS in
  `OpenDocument.Publish` decides which one's parse actually stands.
- `TryGetAnalyzed(path, out document, out result)` — the document AND its latest completed
  analysis, false when either is missing. The cheap resolve: it answers only what the store knows,
  where the server's `NavigationSupport.Resolve` also builds the store, context id and declared
  namespaces. Formatting is the caller.
- `TryAnalyzeFresh(path, ct, out document, out result)` — the document and a parse of the text it
  holds RIGHT NOW, re-analysing when the last one has been overtaken. What the handlers whose
  client has no "ask again" need (document symbols, folding, selection ranges, semantic tokens,
  code actions): a file opened during startup indexing has no published analysis yet, and a
  debounced one describes text the user has already replaced. Five handlers wrote it out by hand,
  each with its own copy of that reasoning.
- `IsOpen(path)` — whether the editor's buffer owns this file, which is the question six callers
  asked as a `TryGet` with a discarded out parameter. Anything that would read the file from disk
  (the lint sweep, a watched-file re-index, the workspace diagnostics publisher) leaves an open
  file to the live-analysis path.

## Resolution/ResolverInsertProvider.cs

- `sealed class ResolverInsertProvider` — the ONLY #insert provider: resolves the raw
  path through the asking file's ResolutionContext, then takes the target from `InsertCache` or
  reads and lexes it. Both the document path and `WorkspaceIndexer` use it; the indexer had a
  private one of its own over a second cache of the same headers until they were merged.

## Resolution/IFileSystem.cs

- `interface IFileSystem` — the thin disk seam (FileExists/DirectoryExists/ReadAllText/
  GetLastWriteTimeUtc/EnumerateFilesWithExtensions) so resolver and indexer tests run on fake
  in-memory trees. `EnumerateFilesWithExtensions` is the only enumeration on it: a single-pattern
  form was carried for a while after its last caller went and cost every implementer a method
  nothing called.
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
  - `GetScriptRelativePath(absolutePath, context)` — the file's identity under its context's root,
    the key an overlay shadows on. Both entry points NORMALIZE what they are given. Unnormalized
    input would come back `""` as `ScriptRecord.RelativePath`, and every import match downstream
    would silently never fire.
  - `Resolve(context, scriptPathWithExtension)` — probes Mod: [mods\m, raw] · Raw: [raw] ·
    Workspace: [base, other folders, raw]; first existing file wins. Rooted paths, drive letters,
    and ".." are rejected. Both slash styles accepted. Memoized by `(context, relative path)`, MISS
    included: an uncached miss walks every root and is asked about on every keystroke by two
    independent callers resolving the same directive list (`FileImports` and `UsingNotFoundLint`) —
    measured as an exact 2x-by-caller, 4x-by-root-count multiplier without the memo.
    `InvalidateResolutionCache()` clears it wholesale on any watched create/delete
    (`WatchedFileUpdater.Apply`, both branches, regardless of language) — coarse, but those events
    are user-paced, and a create can turn a cached miss into a hit just as a delete can turn a
    cached hit into a miss.
  - `EnumerateIndexTargets()` — every .gsc/.csc/.gsh under raw + mods + workspace
    folders, deduplicated (cold-start indexing input).

## Analysis/NamespaceUsageLint.cs

- `static NamespaceUsageLint.Analyze(result, store, language, resolver, askingPath, contextId,
  profile?, imports?)` — a cross-file lint: a qualified call `ns::foo()` should have a `#using` that
  imports a file declaring namespace `ns` (or `ns` be one of the file's own namespaces). Returns Error
  diagnostics (`NamespaceNotImported`) — the script does not link without the import, so it is a
  broken build rather than a style point; it ran as a Warning first while the rule proved itself
  and was promoted after holding at zero across the stock corpus. Zero false positives by construction: it builds the set
  of available namespaces from the file's own `#namespace` blocks plus every `#using` target
  resolved to an INDEXED record, and if any `#using` can't be resolved to a record it suppresses
  the whole lint (a not-yet-known import might supply the namespace). Unqualified calls key under
  the current namespace so they never trip it; `sys::` builtin calls have a null namespace and
  are skipped. Runs on `ResolvesByNamespace` dialects only — the mirror of `IncludeUsageLint`'s gate.
  On a merge dialect `#using` does not lex and `#namespace` is off, so the available set can never
  hold anything but the file's own stem and no edit could clear the Error, while `myutils::func()`
  links there through an `#include`. Merged into open-document diagnostics by the server's TextSyncHandler.

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

- `PreferBooleanLiteralLint.InspectNode(node, builtins, …)` + `InspectRest(…)` — hints that a literal `0`/`1`
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
- Returns before resolving anything on a dialect without `private` (`GameProfile.HasPrivateFunctions`,
  derived from the keyword set — BO3 only). Nothing there can carry the flag, and on a merge dialect
  the two lookups per call were the most expensive thing in a 50,000-file lint pass while reporting
  nothing.

## Analysis/ReadOnlyWriteLint.cs

- `static ReadOnlyWriteLint.Analyze(result, objectFields, typer)` — reports writes to `.size` (Error;
  a language-spec fact) and to engine fields the curated data marks read-only (Warning, since
  that data can carry mistakes). Assignments including compound forms and `++`/`--` all count
  as writes. A field is only flagged when EVERY entity kind declaring the name agrees it is
  read-only, because the owner's kind isn't inferred at this layer.

## Analysis/GlobalObjectWriteLint.cs

- `GlobalObjectWriteLint.InspectNode(node, globals, …)` + `GlobalNames()` — reports an assignment to one of the engine's
  global objects (5035, Error): `level = 1`, `anim = 1`. The names come from
  `GameProfile.GlobalObjectNames`, never a table here, so `world` is a global on BO3 and an ordinary
  local name on CoD4. Only a BARE name counts — `level.things = []` and `game[ "k" ] = 1` write
  THROUGH the object and are how every script in every corpus uses one. `classes` is excluded even
  where the profile lists it: nothing establishes what the compiler does with an assignment to it,
  and an Error has to be certain. Swept clean over 8,289 scripts across all five games.

## Analysis/NodeLintPass.cs

- `internal static NodeLintPass.Run(result, builtins, types, diagnostics)` — ONE walk of the tree
  shared by the nine rules whose judgement is about a single node. Each of those descended the whole
  file on its own, visiting the same million corpus nodes nine times to ask nine independent
  questions; a bare walk that does nothing else is about 85 ms of a 2.1 s bo3 lint pass, so the
  traversal was nearly all of what those rules cost.
- A rule qualifies only when its own walk was PURE PASS-THROUGH. The six left out are named in the
  type's doc comment with the reason each one cannot join — a threaded flag, per-function state, or
  a cache carried down the descent. Read that list before adding a tenth.
- Each rule exposes `InspectNode`, its whole judgement about one node with no descent, so there is
  one copy of each judgement. Diagnostics land in one builder and `WorkspaceLints` sorts by position
  before returning, so merging the walks cannot change what is published.

## Analysis/ — the remaining lints

Each is run per open document by `WorkspaceLints` and merged by the server's `TextSyncHandler`.
Severity is chosen by MEASUREMENT over the corpus, not by taste: a rule reported as an Error must
never land on code that ships and works.

Two entry shapes. A rule that needs its own traversal — per-function state, a flag threaded down the
descent, a cache carried along — exposes `static Analyze(...)` and walks the tree itself. A rule
whose judgement is about a single node exposes `InspectNode` instead and is driven by
`NodeLintPass`'s one shared walk; it has no `Analyze`, because a second walker that nothing but a
test called is how the two silently drift. `NodeLintPass` names which rules are in and why the rest
are out.

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
- `FileImports` — a file's import directives resolved ONCE, shared by the four lints that need them,
  since every resolve is a filesystem probe per root and this runs per keystroke. `Complete` carries
  the bail-out the two import-existence rules (5000, 5026) keep; `Usings` and `Includes` stay APART
  because no dialect has both and one list would let the include rule judge a `#using`.
  `UsingNotFoundLint` deliberately does NOT share it — it asks whether the target exists on DISK,
  which is what decides linking, while this also requires the index to have reached it.
- `ImportGate` — the precondition several lints share: an unresolved `#insert` or `#using` makes the
  set of legal names unknowable, so a rule about to say "this matches nothing" stands down.
  `MacrosLost` is the header half and is NOT the caller's to name — all six ways the preprocessor
  abandons a splice, since each loses the macros identically and `InsertNotFound` alone is merely
  the one anybody remembers. `InsertMissingSemicolon` is excluded because it reports and carries on.
  The `#using` half stays a parameter, since rules differ on whether they already cover it.
- `MacroReports` — the one rule six lints share about a reference a macro expanded into: report it
  once per site, and allocate nothing when no macro is involved. The dedupe KEY stays the caller's,
  because it is each rule's own claim about what it would have you fix.
- `ArgumentCountLint` (5022/5023) — the rule is NOT symmetric. A **script function** is only wrong
  with too MANY arguments: passing fewer is legal and idiomatic, the rest being `undefined`. A
  **builtin** is engine-validated, so its mandatory count is a real lower bound — but only where
  `HasReliableBuiltinSignatures` says the data can carry the claim. The upper bound is absent on
  builtins because the library under-declares variadics; restoring it is a data problem, not a code
  one.
- `CaseLabelLint` (5010/5011/5017) — a `case` on an undefined value, a non-constant label, and the
  same label twice in one switch. The third found a real duplicate `case 1:` in shipped BO3 code.
- `ClassCycleLint` (5021) — a class inheritance cycle, which would otherwise recurse forever.
- `DevBlockCallLint` (5006) — calling a `/# #/`-only function from release code. Resolves a call only
  when its name COULD be dev-only — `LanguageStore.MayBeDevOnly`, or a dev-only builtin — since for
  any other name neither half of the rule can report; the prefilter is what stopped it resolving
  every bare-name call on a merge dialect at scale.
- `ArithmeticLint` (5031, Warning) — division by a divisor WRITTEN as zero. No constant propagation:
  the literal case is the one that needs no data flow to be certain. A `NodeLintPass` rule.
- `ConstDeclarationLint` (5029/5030, Warning) — a `const` whose value is not a compile-time constant,
  and a later write to one. The per-node half rides `NodeLintPass`; `InspectRest` does the
  declaration-level half.
- `ExpressionStatementLint` (5032, Warning) — a statement whose expression cannot do anything
  (`a + b;`, `self.health;`, `x == 1;`) — usually a lost `=` or lost parentheses. The weakest test
  that still catches those. A `NodeLintPass` rule.
- `ThreadedResultLint` (5028, Warning) — the value of a `thread` call used for something: a threaded
  call hands back control at the first `wait`, so the caller gets whatever existed by then. Walks on
  its own because it threads a "value is consumed" flag down the descent.
- `TypeMismatchLint` (5033/5034, Warning) — a non-array enumerated, and a vector component that cannot
  be a number: the two findings the union type lattice made answerable. Reads the flow typer's shared
  inference; a `NodeLintPass` rule.
- `DuplicateImportLint` (5018) — the same file imported twice, tagged `Unnecessary` so the line
  greys out. Separator and case differences do not make it a different file.
- `UnassignedVariableLint` (5016/5024) — a local read that nothing in the function writes. Excludes
  parameters, loop bindings, `waittill` outputs, profile globals, file-scope constants, macro-supplied
  names, and the `...` parameter pack; reports 5024 instead when the pack is read in a function that
  does not declare `...`. An unresolved import stands the whole rule down.
- `UnreachableCodeLint` (5015) — statements after a `return`/`break`/`continue`. **Information**,
  not a Hint: it carries no `Unnecessary` tag, so a Hint would produce no visible output at all, and
  the corpora afford the panel — 48 findings in 42 files across all five games.
- `UnusedBindingLint` (5020) — a parameter or `waittill` output nothing reads. A **Hint**, so it
  never reaches the Problems panel and the fade is the entire output: at any panel-visible severity
  it would report 5,277 findings on BO3's own scripts, most of them engine-fixed callback signatures.
- `UnusedIncludeLint` (5012) / `UnusedUsingLint` — an import contributing nothing. 5012's test is
  MARGINAL, not direct, and that is what stops a Hint manufacturing an Error: a file may include a
  hub purely as a conduit, and judging the directive by what its TARGET declares would call that
  unused, offer "Remove", and the removal would make 5026 fire. It is measured against what is
  CERTAINLY kept rather than against the other candidates — otherwise two conduits each cover the
  other and both are declared removable, which the bulk "remove all" action would then act on. 46
  stock directives across CoD4, MW2 and WaW were in that state.
- `UnusedLocalLint` (5008) — a local assigned and never read. A **Hint** for the same reason 5020
  is: as Information it would put 4,711 entries in the Problems panel across the five games — 1,716
  on MW2 alone — in code that ships and works. The `Unnecessary` fade is the useful half.
- `UsingNotFoundLint` (5009) — an import naming no file, `#using` or `#include` alike. One code for
  both: no dialect has both spellings, and "Cannot find script" is the same sentence either way. The
  `#include` half matters doubly: 5026 stands down on an unresolvable include, so without this a
  transposed letter would switch off the merge dialects' only import Error and say nothing about
  why.
- `VoidResultLint` (5019) — keeping the result of a builtin that returns nothing. Only builtins:
  GSC declares no return type, so the same claim about a script function would be a guess.
- `GameShapeDetector` — not a lint but the mismatch check behind it: reads a file's directives to
  judge which family it looks like, and reports when the selected profile disagrees.
- `WorkspaceLints` — the composition point that runs the cross-file rules for a document.
- `LintScope` / `LintTimings` — one timing wrapper per rule, opening the instrumented build's
  `PerfTracker` scope and, when a caller passes a sink, recording the same span in an ordinary
  build. The second half exists because `PerfTracker` is `[Conditional]`: the corpus budget gate
  that asserts no rule crosses its share of the keystroke debounce runs in a normal Release build,
  where a tracker-based measurement would silently be no measurement at all. `LintScope` is a
  struct, and a null sink costs a null check per rule.

## Resolution/InsertCache.cs

- `InsertCache` — the lexed `#insert` headers, shared across every file that inserts them, plus what
  each header CONTRIBUTES (`IHeaderMacroCache`). Keyed by the RESOLVED absolute path, never the path
  as written: `scripts\shared\shared.gsh` means the mod's copy when a mod file asks and raw's when a
  raw file asks, so keying on the written path would let whichever file asked first decide the
  contents for everyone.
- Validated by last-write time rather than by an invalidation message, because a watcher that drops
  an event leaves a stale header — and a stale header changes what macros expand to with no error to
  trace back. A failed read is not cached. See `PERF.md` for what the two caches were worth.
- `SeedIfAbsent` takes a header the indexer has already read and lexed as one of its own targets,
  rather than letting the insert path build it a second time. `TryAdd`, not an assignment: a `.gsc`
  inserting the header may get there first, and its entry is equally current and may already carry a
  walked contribution. The stamp must be read BEFORE the content it describes.

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
  different files in different contexts). Reads the files writing the path from both language
  stores' and the header store's `DirectiveIndex`, since a `.gsh` can insert another. `ToDirectivePath` encodes the asymmetry that `#using` names
  a script without its extension while `#insert` keeps the `.gsh`.

## Typing/BuiltinEmulations.cs

- `static BuiltinEmulations.TryGetReturnType(name, out type)` — return types for the callable
  KEYWORDS, which carry no entry in the bundled API and would otherwise type as Unknown.
  Deliberately two entries: of the keywords absent from the API, only `isdefined` (bool) and
  `vectorscale` (vector) yield a value worth typing; the rest are statement-shaped, and in this
  lattice a void result is indistinguishable from Unknown.

## Typing/FlowTyper.cs

- `readonly record struct InferredAssignment(NameRange, Value, Name, IsFirstForName, IsField)` — the
  inferred value of a local or field at ONE assignment site. Every typed assignment is recorded
  and each consumer filters: inlay hints take `IsFirstForName` only, hover takes them all so it can
  report the type as of the cursor. `IsField` separates `self.count = 1` from a local of the same
  name.

  Carries the whole `ScrValue`, with `Type` (the projection) and `Display` (the label) as computed
  properties. Storing the projection instead threw away the two facts only display wants: a
  `new Foo()` reached the editor as `struct` and a `#"str"` as `int`, both computed correctly and
  both discarded at this boundary. A consumer judging a type still reads `Type`.
- `readonly record struct LocalTypeHover(Name, Range, Value)` — the value of the local identifier
  under a cursor, consumed by hover. Same `Type`/`Display` split, for the same reason.
- `readonly record struct FieldWrite(NameRange, FieldName, OwnerType, Value)` — one `owner.field = …`
  write with the owner's inferred VALUE at that point (a whole `ScrValue`, not the coarse `ScrType`
  — `self` is a real `Entity|Struct` union with no single projection, so a consumer needs
  `MayBe`/`IsUnknown` rather than exact equality to still recognise it as a possible entity),
  consumed by `ReadOnlyWriteLint` and `PreferBooleanLiteralLint`. `Value` is null for a compound
  write or `++`/`--`, which have no single assigned value.
- `sealed class FlowTyper` — a deliberately-small forward type-flow pass, per function.
  `InferAssignments(ParseResult)` walks each function/method body with a per-function
  local environment (`name → ScrValue`), recording every assignment that resolves to a concrete
  type. `TryGetLocalTypeAt(result, position)` finds the innermost identifier under a
  cursor and its enclosing function (via `AstSearch.TryFindLocalContext`) and returns the local's
  inferred type when one exists — so a hover always agrees with the inlay hint at the assignment.
  `TypeOf` types literals, parenthesised/vector/array/`new` expressions,
  identifiers (earlier locals, then the globals `self`/`level`/`world`/`anim`/`game`),
  `&foo` and `[[ ptr ]]` (which carry a `ScrFunctionRef` naming the function, so a call through a
  pointer can be connected to the declaration behind it) and an arrow call's object,
  prefix and binary operators via `ScrOperators`, builtin call return types unioned across every
  overload, and field access `owner.field` (`.size`→int; else the engine object-field data seeds a
  type, but only when every entity kind declaring the field name agrees — the owner's kind isn't
  inferred). Anything uncertain stays unknown and produces no hint — the zero-false-positive rule.
  Script-function return inference is out of scope (their bodies aren't re-typed here). Constructed
  with the per-language `BuiltinApi` and the shared `ObjectFields`.
- **The environment holds `ScrValue`, not `ScrType`.** Everything above describes what the editor
  sees, which is the coarse `ScrType` projection at the public boundary. Underneath, the walk carries
  unions (`int|string` where the projection says Unknown) and folded constants.
- `ScriptTypes` + `FlowTyper.InferValues(result)` — the per-node surface: the value of every
  expression the walk touched, keyed by node REFERENCE (AST nodes are records, so structural equality
  would make the three zeroes in `( 0, 0, 0 )` one key). The field-write and type-mismatch lints and
  the pointer-call inlay hints read it. `TryGetValueAt` is the position query that returns the union
  rather than the label, for go-to-type-definition.

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
- `MacroExpansionPreview.cs` — renders a readable, length-limited macro body for hover and signature
  help, and substitutes call-site arguments token-by-token rather than by unsafe text replacement.
  The body keeps the LINES it was written on — the backslashes are gone from it by then, but each
  token still carries its own line — and indentation is rendered as ranked LEVELS four spaces apart
  rather than as the author's columns, since a tab is one character in a range and subtracting
  columns would give a tab-indented header a one-space step. One argument scan serves both readers:
  `ArgumentsFollowing` gives hover the text, and `ArgumentSpansFollowing` gives the macro inlay
  hints the trimmed `MacroArgumentSpan` offsets a label is placed at. Nesting counts brackets as
  well as parentheses, and an unterminated list — the normal state while typing — yields what has
  been written so far.
- `StockScripts.cs` — loads the profile's raw-relative stock-script list and canonicalizes slash
  style and casing for the raw-file warning setting.
