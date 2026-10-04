# Architecture guide — the mental model

This is the explanation `server/ARCHITECTURE.md` assumes you already have. Read it once, end to end,
before changing anything. The rules that must not be broken are collected separately in
[invariants.md](invariants.md); the step-by-step flows are in [lifecycles.md](lifecycles.md).

---

## 1. The problem GSCode solves

GSC is the scripting language of the Call of Duty engines. GSCode is a VS Code extension and a
language server for it: diagnostics, navigation, completion, refactoring and formatting, across five
games whose dialects differ.

The language facts that shape the whole design:

- **Three file kinds.** `.gsc` runs on the server VM, `.csc` on the client VM, `.gsh` is a header of
  macros and code spliced in by `#insert`. A `.gsc` and a `.csc` are two separate worlds: a function
  in one is not visible from the other, even with the same name. A header serves both.
- **Three places a script can live.** The game's stock scripts (the **raw** folder), a **mod**
  (`mods\<name>`), or a loose **workspace** folder. A mod file at the same relative path as a raw
  file **shadows** it (an **overlay**). A mod sees itself and raw; it never sees another mod. This is
  the **context** of a file, and every lookup is filtered by it.
- **Two import models.** Black Ops III uses `#using` + `#namespace`: calls are qualified
  (`util::foo()`), and a function's identity includes its namespace. Every earlier game uses
  `#include`, which **merges** the included file's functions into the caller's scope: calls are bare
  or path-qualified (`maps\mp\_utility::foo()`), and identity is the bare name.
- **Five dialects.** CoD4, WaW, MW2, BO1, BO3. Keywords, directives, pointer syntax, doc-comment
  style, global objects and bundled engine data all differ. All of it is data on a `GameProfile`.
- **Names are case-insensitive**, except macro names, which are case-sensitive.

A large stock workspace is about a thousand scripts; the design target is 50,000 files with
per-keystroke work staying flat.

---

## 2. The big picture

```
 VS Code                                  GSCode.Server (one process per window)
┌──────────────────────┐   named pipe   ┌─────────────────────────────────────────────────┐
│ client/ (TypeScript) │◄──────────────►│ OmniSharp LSP host                              │
│ - spawns the server  │  LSP + gscode/*│  Handlers/ (thin)  ──►  NavigationSupport        │
│ - status bar, cmds   │                │                         FormattingSupport        │
│ - TextMate grammar   │                │                         DocumentLinter           │
└──────────────────────┘                │  Startup/ Composition/ Configuration/ Transport/ │
                                        └───────────────┬─────────────────────────────────┘
                                                        │ calls (no LSP types cross this line)
                                        ┌───────────────▼─────────────────────────────────┐
                                        │ GSCode.Workspace                                │
                                        │  Documents/DocumentStore   (open buffers)       │
                                        │  Database/ScriptDatabase   (closed-file records)│
                                        │     ├─ LanguageStore Gsc  + 9 indexes           │
                                        │     ├─ LanguageStore Csc  + 9 indexes           │
                                        │     └─ GSH header store   + 3 indexes           │
                                        │  Resolution/PathResolver   (raw/mod/workspace)  │
                                        │  Indexing/WorkspaceIndexer (+ WatchedFileUpdater)│
                                        │  Cache/SqliteCache         (records on disk)    │
                                        │  Analysis/ (lints)  Completion/  Typing/  Api/  │
                                        └───────────────┬─────────────────────────────────┘
                                                        │ pure function calls
                                        ┌───────────────▼─────────────────────────────────┐
                                        │ GSCode.Parser  Lexer → Preprocessor → Parser →  │
                                        │                SymbolExtractor  = ParseResult   │
                                        └───────────────┬─────────────────────────────────┘
                                        ┌───────────────▼─────────────────────────────────┐
                                        │ GSCode.Core  text, positions, diagnostics,      │
                                        │              SymbolKey, ScrValue, GameProfile   │
                                        └─────────────────────────────────────────────────┘
```

Dependencies point down only. `GSCode.Server` is the only project that knows LSP exists. Below it,
everything is plain .NET and testable without a protocol.

---

## 3. The data model, from text to answers

Follow one file through the layers. Each arrow is a function in the per-file pipeline
(`ScriptAnalysis.Analyze`) or the database.

```
string ──► SourceText ──► Token[] ──► PToken[] ──► ScriptNode (AST) ──► ExtractionResult
           (Core)        (Lexer)    (Preprocessor) (Parser)             (SymbolExtractor)
                                                                              │
                         ParseResult = all of the above + diagnostics ◄──────┘
                                │
              open file: kept whole in DocumentStore (OpenDocument.LatestResult)
                                │
                     ScriptDatabase.Commit / BuildRecord
                                ▼
                         ScriptRecord  (immutable, per file, no syntax tree)
                                │
                  LanguageStore.Upsert: swap record, diff every index
                                ▼
       ReferenceIndex · DeclarationIndex · NamespaceIndex · ClassGraph · RelativePathIndex
       DependentsIndex · DirectiveIndex · PathTreeIndex · VocabularyIndex
```

The pieces worth knowing by name:

- **`SourceText`** (`Core/Text`). Immutable text with a line index. All offsets and positions are
  UTF-16 code units, which is the LSP default, so mapping is identity. Ranges are half-open.
- **`Token`** (`Parser/Lexing`). Kind + span, no text. Trivia (whitespace, comments) stays in the
  lexer's stream for the formatter and semantic tokens.
- **`PToken`** (`Parser/Preprocessing`). A token after macro expansion and `#insert` splicing, with
  interned text and a **`Provenance`**: which file it really came from, and which root-file range a
  diagnostic about it should point at. This is how a header's code can be parsed as part of the
  script while hover and go-to-definition still land in the header.
- **AST** (`Parser/Syntax/Ast`). Immutable records. Ranges are in root-file coordinates. Error
  recovery always produces a tree that covers the file (`ErrorNode`), because half-typed code is the
  normal state of an editor.
- **`ExtractionResult`** (`Parser/Extraction`). The cross-file surface: namespaces, functions,
  classes, macros, and a flat list of classified **references** (`ReferenceEntry`: a `SymbolKey`, a
  range, a `ReferenceKind` such as Definition, Call, FieldWrite).
- **`SymbolKey`** (`Core/Symbols/SymbolKey.cs`). The cross-file identity of a name: namespace,
  name, kind, owner class. Lowercase-interned. On a merge dialect a function's key has no namespace
  (`GameProfile.KeyNamespace`). Language is not in the key; isolation is structural (separate stores).
- **`ScriptRecord`** (`Workspace/Database/ScriptRecord.cs`). Everything known about one file without
  its syntax tree: path, language, context id, relative path, content hash, symbols, references,
  dependencies, field bindings, diagnostics. A closed file has only this. Records are replaced
  whole, never mutated.
- **`LanguageStore`** (`Workspace/Database/LanguageStore.cs`). One language world: a path → record
  map plus nine inverted indexes, all updated in the same per-file diff that swaps a record in. A
  query reads an index to find candidate files, then reads only those records.

Open files and closed files are answered differently on purpose. An open file has its whole
`ParseResult`, which is what position-based features need. A closed file has only its record, which
is all a cross-file question needs, and keeps memory bounded.

---

## 4. Who owns what state

Almost everything is a DI singleton built once in `Composition/ServerServices.cs`. Things that must
exist before the container, or that are replaced during the session, sit in a **holder** so
consumers read the current value at call time.

| State | Owner | Lifetime / notes |
|---|---|---|
| Active game | `GameProfile.Active` (static) | Selected once at startup from `--game`. Process-global; tests that change it must restore it (`ProfileScope`). |
| Settings | `ServerSettings` | Applied at initialize and on every configuration push. |
| Path resolver | `ResolverHolder.Current` | Rebuilt when workspace folders change. Read once per operation into a local. |
| Open documents | `DocumentStore` | One `OpenDocument` per open file: text, version, latest `AnalysisSnapshot`. |
| Indexed records | `ScriptDatabase` | GSC store, CSC store, shared GSH store. |
| Header cache | `InsertCache` | Lexed `.gsh` files keyed by resolved path, validated by last-write time. Shared by documents and the indexer. |
| Persistent cache | `CacheHolder` → `SqliteCache` | Opened in `OnStarted`, closed at exit or by `gscode/clearCache`. |
| Indexing task | `IndexingLifetime` | One cancellation token for the whole startup task. |
| Early notifications | `ConnectionSettleGate` | Defers sends until the pipe has settled (500 ms). |
| Bundled game data | `BuiltinApiSet`, `ObjectFields`, `StockScripts` | Lazy singletons: loaded on first use, after the game is selected. |
| Flow-typer results | `FlowTyper.InferValuesShared` | Weakly keyed by `ParseResult`; one walk per document version, shared by lints, hints and hover. |

---

## 5. Concurrency model

The server is concurrent in a few deliberate places and nowhere else.

- **LSP handlers** run on OmniSharp's threads. Request handlers are synchronous reads over immutable
  data.
- **Document analysis** runs on the thread pool, debounced 250 ms per edit, and is **single-flight
  per document** (`SingleFlightAnalysis`). Two analyses of one document can still overlap (the
  debounced one and a request thread re-analysing a stale document); the version compare-and-swap
  in `OpenDocument.Publish` decides which one stands, and diagnostics are stamped with the version
  that was analysed.
- **Indexing** is a `Parallel.ForEachAsync` over every core but one. Commits to a store go through
  64 path-striped write gates; each index shards its own dictionary 64 ways. No invariant spans two
  keys, which is what makes the sharding sound.
- **The cache** has one background writer. Serialization happens on the indexing threads, so the
  writer does only SQL.
- **Cancellation** is threaded everywhere a superseded result would waste work: a newer edit
  cancels the pending analysis, and read paths that re-analyse take a required `CancellationToken`.
- **Records are immutable** and swapped whole, so a reader never sees half an update.

---

## 6. The dialect seam

`GameProfile` (`Core/GameProfile.cs`, registry in `Core/Profiles/SupportedProfiles.cs`) holds every
fact that differs between games: keywords (a word is a keyword only if the profile lists it), import
style, pointer style, doc-comment style, global objects, script extensions, raw-folder layout,
bundled data file names, and capability flags that rules gate on. Code asks the profile; it never
branches on a game name.

Five profiles are **Supported** and **Verified** against the game's own scripts. Thirteen more are
**cores**: named identities with the shared base dialect and nothing game-specific, waiting for
someone with that game's tools to fill them in. `server/GAME_PROFILES.md` is the full matrix and the
evidence; the `add-game-profile` skill is the procedure.

---

## 7. The performance model

Two rules carry most of it:

1. **Nothing a keystroke or a request pays for may walk every record.** A query that needs "every
   file that …" gets an index in `LanguageStore`, maintained in the same diff. The scale suite
   (10,000–50,000 generated files) proves per-request costs stay flat.
2. **Measure before and after.** Every optimisation in `server/PERF.md` has a number, and several
   entries record an idea measured and rejected. Budgets are asserted, not hoped for: no single lint
   may take more than 40% of the debounce on its worst stock file (`LintBudgetTests`).

The other levers are already pulled: string interning (`NameTable`), a binary record format for the
cache, struct enumerators for AST walks, one shared walk for nine node rules (`NodeLintPass`), one
flow-typer pass per document version, and Server GC with a compacting collection after indexing.

---

## 8. Where new code goes

| You are adding… | Put it in | Then |
|---|---|---|
| A new syntax form | `GSCode.Parser/Syntax` (+ `Ast/`, `AstSearch.ChildrenOf`, `AstPrinter`) | Gate it on a `GameProfile` flag if not every game has it |
| A per-file error the tree alone decides | `Parser` or `SymbolExtractor` (3xxx/4xxx) | `add-diagnostic` skill |
| A rule that needs other files | `GSCode.Workspace/Analysis/<Name>Lint.cs`, called from `WorkspaceLints.LintsOnly` | `add-diagnostic` skill; corpus sweep before shipping |
| A one-node rule | An `InspectNode` on the rule, driven by `NodeLintPass` | Read `NodeLintPass`'s doc comment first |
| A new cross-file question | A `DatabaseQueries` method backed by a `LanguageStore` index | Never a loop over `AllRecords` |
| A new LSP feature | A thin handler in `Server/Handlers`, registered in `ServerServices.Configure` | `lsp-handler` skill |
| A client ↔ server message | A `gscode/<name>` handler on the server, `sendRequest`/`onNotification` in `client/src` | Add it to the table in [features.md](features.md) |
| A setting | `client/package.json`, `client/src/settings.ts`, `ServerSettings` | `SettingsReachTheServerTests` checks the first two agree |
| Game-specific behaviour | A field on `GameProfile`, set in `SupportedProfiles.cs` | `add-game-profile` skill |
| Engine function or field data | `server/tools/field-data/sources/curated/` → regenerate | `regenerate-game-data` skill |
| A field on `ScriptRecord` | Both halves of `RecordSerializer`, and bump `CacheSchema.RecordFormatVersion` | `RecordSerializerTests` fails until you do |
