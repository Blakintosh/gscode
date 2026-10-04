# Glossary

Terms as this codebase uses them. Where a term has a type, the type is named so you can search for
it.

**Analysis** — one run of the per-file pipeline (`ScriptAnalysis.Analyze`) over one file's text,
producing a `ParseResult`. For an open document it is stored as an `AnalysisSnapshot` (result +
the version it describes).

**Autoexec** — a function the engine calls when its script loads. It may have no callers in any
script; CodeLens says "autoexec entry point" rather than "0 references".

**Builtin** — a function provided by the engine, not declared in any script. Loaded per game and per
language from `server/src/GSCode.Workspace/Api/<prefix>_api_<gsc|csc>.json` into `BuiltinApiSet`.

**Build identity** — `ServerBuildIdentity.Compute`: a hash of the game, the server assemblies and the
bundled data. A cache written by a different identity is wiped.

**Cache** — the per-workspace SQLite file at `%APPDATA%/gscode/cache/<hash>.db` holding serialized
`ScriptRecord`s, so a restart restores unchanged files instead of parsing them (`SqliteCache`).

**Cold / warm start** — indexing with an empty cache / with a cache whose records still match the
files on disk.

**Context** — which world a file belongs to: Raw, Mod (with a name) or Workspace
(`ResolutionContext`). Written as a context id: `raw`, `mod:<name>`, `workspace:<folder>`. Decides
what the file can see.

**Core profile** — a `GameProfile` that names a game but carries only the base dialect. Not
selectable. See Supported profile.

**Corpus** — a real game's stock scripts, pointed at by `GSCODE_CORPUS_<GAME>`. Corpus tests run
the pipeline over every one of them. A corpus test with no variable set passes without doing
anything.

**CSC / GSC** — client-side and server-side script. Two separate language worlds, two
`LanguageStore`s. WaW, BO1 and BO3 have `.csc`; CoD4 and MW2 do not.

**Debounce** — the 250 ms wait after a keystroke before analysis runs
(`AnalysisTiming.DebounceMilliseconds`). Also the unit lint budgets are stated in.

**Dependents** — the files whose diagnostics may change when a file's exported surface changes.
Re-linted by `DependentDiagnosticsRefresher`.

**Dev block** — `/# … #/`. Code that runs only with developer scripts enabled. A function declared in
one is dev-only (5006 if called outside one).

**Dialect** — the language variant of one game: keywords, directives, import style, pointer style.
Represented by a `GameProfile`.

**Export signature** — a hash of what other files can observe about a file: namespaces, function and
class names, parameters, visibility (`ExportSignature`). A body edit does not change it, so it does
not re-lint other files.

**Field** — `owner.name`. Declared nowhere; it comes into existence by being written. Its "definition"
is its writes (`ReferenceKind.FieldWrite` / `FieldUpdate`). Engine fields with known types come from
`ObjectFields`.

**Field binding** — what a field write puts in the field when that is a class (`new Foo()`) or a
function (`&foo`). Stored on the record so go-to-implementation can follow callbacks across files.

**Flow typer** — `FlowTyper`: a forward type-flow pass per function, producing a `ScrValue` per
expression. Feeds inlay hints, hover, go-to-type-definition and three lints.

**Full / partial / off** — `gscode.workspaceIndexingMode`. Partial indexes everything for navigation
and lints open files; full also lints every closed file; off indexes nothing.

**GSH** — header file, inserted with `#insert`. BO3 only. Lives in its own store, shared by both
language worlds.

**Harvest** — a corpus sweep that writes a list (e.g. calls resolving to nothing) as input for
curating game data. Files in `server/tests/GSCode.Server.Tests/harvest/`.

**Include / merge dialect** — the games before BO3. `#include` merges a file's functions into the
caller's scope; functions are keyed by bare name.

**Index** — two meanings. (1) The startup pass that analyses every script into records
(`WorkspaceIndexer`). (2) One of the inverted indexes in a `LanguageStore` that answer a question
without walking all records.

**Insert** — `#insert path.gsh;`. Splices a header's tokens into the file at that point, done by the
preprocessor through an `IInsertProvider` (`ResolverInsertProvider` in the server).

**Lint** — a rule in `GSCode.Workspace/Analysis/` producing 5xxx diagnostics, usually needing other
files.

**Namespace dialect** — BO3. `#using` imports, `#namespace` declares, calls are qualified, and a
function's key includes its namespace.

**Node lint** — a rule judging one AST node at a time, driven by the shared walk in `NodeLintPass`.

**Overlay / shadowing** — a mod file at the same relative path as a raw file replaces it for that
mod. Queries apply this per record (`HasOverlayAt`).

**Path call** — `maps\mp\_utility::foo()`: a call naming the file directly. Pre-BO3 only.

**Pragma** — `// #pragma disable <code>` … `// #pragma restore <code>`: suppresses diagnostics by
code in a region (`PragmaDirectives`).

**Profile** — `GameProfile`. Every fact that differs between games.

**Provenance** — on each `PToken`: the file the token really came from and the root-file range to
report against. How inserted and macro-expanded code stays navigable.

**Raw** — the game's stock scripts folder (`share\raw` on BO3, `raw` before). Read-only in spirit:
saving there can warn (`gscode.rawFileWarningMode`).

**Record** — `ScriptRecord`: the immutable per-file summary kept in the database. A closed file has
only its record.

**Reference** — one classified occurrence of a name: `ReferenceEntry` = `SymbolKey` + range +
`ReferenceKind`. The reference index maps keys to the files containing them.

**Relative path** — a file's path under its context root, the form `#using` / `#include` write and
the identity an overlay shadows on.

**Sample** — a hand-written script per game in `server/samples/`, with `// expect` comments that
`SampleScriptTests` checks.

**Scale run** — generated workspaces of 10,000–50,000 files, measuring that per-request costs stay
flat (`Category=Scale`).

**ScrType / ScrValue** — the type model. `ScrValue` is the full lattice (a set of possible types, a
folded constant, truthiness, class or function identity). `ScrType` is its one-word projection for
display; a union projects to `Unknown`.

**Store** — `LanguageStore`: one language world's records and indexes. `ScriptDatabase` holds the
GSC store, the CSC store and the GSH header store.

**Supported / Verified profile** — a game users can select (Supported) whose dialect has been proven
against its own stock scripts (Verified). Five today: cod4, waw, mw2, bo1, bo3.

**Sweep** — running the full diagnostic pipeline over a corpus and reporting by code
(`CorpusDiagnosticSweepTests`, `scripts\sweep.bat`). Also the `full`-mode closed-file lint pass
(`WorkspaceLintSweep`).

**SymbolKey** — the cross-file identity of a name: namespace, name, kind, owner class.
Lowercase-interned except macros and string literals.

**Workspace-only mode** — no raw root found or configured. A normal state: files resolve against the
workspace folders alone.
