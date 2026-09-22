---
name: lsp-handler
description: Add or change an LSP handler in GSCode.Server. Use when touching hover, completion, definition, references, rename, semantic tokens, formatting or diagnostics — it covers stale-analysis rules, the shared reference query, and the failure modes that only appear in a live editor.
---

# Working on an LSP handler

## Stale analysis is the recurring bug

Analysis is debounced **250 ms** behind keystrokes, so `document.LatestResult` routinely describes
text the client has already changed.

**Anything positional must freshen.** Use `_documents.AnalyzeIfStale(document)`, or
`NavigationSupport.ResolveFresh(uri)` where available. Freshen if the handler's answer is a
line/character — completion, signature help, semantic tokens, formatting edits — because those land
on the wrong characters and then appear to fix themselves on the next keystroke, which is why the
symptom always reads as intermittent rather than broken.

`Resolve` (unfreshened) is fine where the answer is a symbol rather than a position.

**Pass the request's cancellation token through.** `Resolve`/`ResolveFresh` REQUIRE one on purpose:
`ResolveFresh` runs a whole lex, preprocess, parse and extract on the request thread, and a request
the client has already cancelled — signature help retriggers on every `,` — used to be analysed to
the end anyway.

**Position → symbol goes through `NavigationSupport.ResolveHit`.** Every position-based handler
used to call `SymbolAtPosition.Resolve` itself: nine copies of one line. What a handler does with a
LOCAL when the hit misses stays per-handler, since the answer's shape differs.

**A file that is not open is answered from its record** — `ResolveForQuery` returns a
`SymbolQueryContext` with no parse. The hierarchies need it: the file an incoming call or a
supertype names is normally not open, and requiring an open document answered those with null,
which the protocol reads as "there are none".

**Text is not the only input that goes stale.** A parse also expands whatever the `#insert`ed
headers said at the time, so editing a GSH invalidates every dependent's parse without changing a
character of it. `AnalysisSnapshot` therefore carries `IHeaderMacroCache.Generation` beside the
document version, and `AnalyzeIfStale` re-parses when either has moved. That check is passive:
something still has to PUSH the refresh, because hover and the rest read `LatestResult` and never
ask. Two things do — `ExportSignature` covers a header's whole content hash, so a header changing
on disk moves it; and `TextSyncHandler` invalidates the insert cache and schedules a dependent
refresh when a GSH is SAVED, which is when its edits become visible to the files reading it from
disk. Remove either and the old symptom returns: the macro's value updates only once something is
typed into the dependent.

## Publishing diagnostics

- **Stamp a publish with the version the analysis RAN on**, never `document.Version` read at publish
  time. Analyses finish out of order — the debounced pass, the save path and a request's freshen can
  all be running — and a late one stamped live puts old diagnostics on screen under a new version.
  `DiagnosticsPublisher` drops a publish older than what is on screen and anything for a document
  that has closed.
- **One URI spelling per file.** The sync handler published under the client's URI while other paths
  built one from the normalized path, which lowercases on Windows; two spellings are two independent
  marker sets in the client. `DiagnosticsPublisher` remembers the client's URI per path.
- **One analysis in flight per document** (`SingleFlightAnalysis`); a request arriving mid-analysis
  queues a rerun rather than starting a second.
- Closed files belong to `WorkspaceDiagnosticsPublisher`, which sends a file only when its
  diagnostics changed. Open files belong to `TextSyncHandler`; neither publishes for the other's.

## Diagnostics: one reporter per cause

A cause is reported by exactly one layer. The preprocessor reports a missing `#insert`; the lint
that would otherwise report every macro from that header as an unknown function stands down
instead. If you find yourself adding a second diagnostic for a condition already reported, the
answer is usually to suppress the downstream one.

## References, rename, CodeLens and highlight share one query

`NavigationSupport.FindAllReferences` backs find-references, rename, document highlight **and** the
CodeLens count. They must stay on it: narrowing one without the others reproduces the
count-versus-peek disagreement the shared query exists to prevent.

It narrows through `DeclaringFile` — which file the key means FROM THIS DOCUMENT — and when that is
one file it reads only the files that can reach it (`DatabaseQueries.FindReferencesReaching`, via the
dependents index) instead of every file mentioning the key; otherwise it collects everything and
applies `ScopeToIncludeGraph`. Both attribute **per reference** — a path call names its file
outright, and a bare name resolves locally first. Filtering whole FILES is not sufficient, and was
wrong twice. `ReferenceScopeCorpusTests` holds the two paths to identical answers; change one and
run it.

## Never walk the store on a request

A handler answers per keystroke or per request, so its cost must not grow with the workspace. Any
loop over `LanguageStore.AllRecords` (or `ScriptDatabase.AllRecords`) on a request path is one that
was fine at 1,000 files and costs hundreds of milliseconds at 50,000 — completion, CodeLens and one
file's lint pass all were, before the indexes (PERF.md, the scale section). Ask the store instead:
`FilesDeclaring`, `FilesReferencing`, `FilesAt`, `FilesNaming`, `VisibleLiterals`, and so on; if the
question has no index, add one to `LanguageStore`'s diff rather than a loop. Pass
`LookupFunctions` a `limit` when you only need to know whether a name resolves (1) or resolves to
one declaration (2).

Measure a handler at scale with `Category=Scale` (`GSCODE_SCALE_SIZES=10000,50000`): its row must be
flat between the two sizes. `HandlerCostTests` covers the densest stock files.

## Renameability is ownership, not kind

What the scripts define can be renamed; what the engine defines cannot, because rewriting the call
sites while the engine keeps the old name turns working code into code that resolves to nothing.
`RenameHandler.IsRenameable` consults the builtin library and the object-field data, and
`PrepareRenameHandler` shares it so the preview and the rename cannot disagree.

## Half-typed code is the normal state

A handler runs on every keystroke, so it sees declarations mid-word constantly. A function whose
name is still empty is not a fault. LSP rejects an empty `DocumentSymbol` name and fails the
**whole request**, so one half-written declaration took the entire outline down with it — filter at
the single point symbols are constructed, not at each call site.

## Semantic tokens

Two things are easy to get wrong and both present as flickering colour:

- `GetSemanticTokensDocument` must return the **same instance per file** across requests. It is the
  delta baseline; a fresh one means every delta is computed against nothing.
- A semantic token **overrides** the TextMate grammar across its range. Do not emit one where the
  grammar already knows better — comments are left entirely to it for this reason.

## Settings the server reads once

`--game`, `gscode.rawPath`, `gscode.modsPath` and `gscode.raw.enabled` are read at startup and
cannot be picked up later. `gscode.restartServer` does **not** help: the launch arguments and
initializationOptions are captured when the LanguageClient is constructed, so a restart relaunches
with the settings the session began with. `reloadPrompt.ts` offers a window reload instead.

If you add a setting in this category, add it to `RESTART_REQUIRED` in `client/src/reloadPrompt.ts`.

## Client-side plumbing

A setting reaches the server only if it is named in **both** `client/package.json`'s
`contributes.configuration` and `client/src/settings.ts`. The explicit list in `settings.ts` is
deliberate — a setting missing from it silently never arrives and the server uses its own default.
