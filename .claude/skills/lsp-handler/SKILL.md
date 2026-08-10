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

## Diagnostics: one reporter per cause

A cause is reported by exactly one layer. The preprocessor reports a missing `#insert`; the lint
that would otherwise report every macro from that header as an unknown function stands down
instead. If you find yourself adding a second diagnostic for a condition already reported, the
answer is usually to suppress the downstream one.

## References, rename and CodeLens share one query

`DatabaseQueries.FindAllReferences` backs find-references, rename **and** the CodeLens count. They
must stay on it: narrowing one without the others reproduces the count-versus-peek disagreement the
shared query exists to prevent.

On merge dialects the results need `ScopeToIncludeGraph`, which attributes **per reference** — a
path call names its file outright, and a bare name resolves locally first. Filtering whole FILES is
not sufficient, and was wrong twice before this.

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
