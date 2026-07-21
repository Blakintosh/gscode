# Post-rewrite follow-ups

**P0–P14 are complete.** This file holds only what still needs a decision. Anything finished
has been removed — its record lives in the git history, and its outcomes are documented where
they belong: `tests/PORTING.md` (every v1 test class resolved), `PERF.md` (measured budgets,
the cold/warm memory answer, the corpus category), and `ARCHITECTURE.md` (structure and the
per-project `FOLDER.md` convention).

---

## Open — optional, nothing depends on them

### 1. `apiUpdate.ts` — opt-in online refresh of the builtin API

Fetch a newer builtin-function library from gscode.net instead of waiting for an extension
release. Gated by `gscode.apiUpdate.enabled` (default off); the bundled JSON is always the
fallback.

**Not blocked — the contract already exists.** `site/src/routes/api/getLibrary/+server.ts` was
written for exactly this:

```
GET https://www.gscode.net/api/getLibrary?gameId=t7&languageId=gsc|csc
```

It serves the same shape we bundle, and today the files are byte-identical (2,892,926 bytes
each). The payload carries its own version marker, so "is there something newer" is answerable:

```json
{ "gameId": "t7", "languageId": "gsc", "revision": 32,
  "revisedOn": "2026-03-29T12:54:56.510Z", "api": [ …2,191 entries ] }
```

Three things to settle before building it:

- **Conditional fetch needs a site change.** Each library is ~2.89 MB, so ~5.8 MB per refresh.
  The endpoint only returns the full payload, so there is no way to check `revision` without
  downloading everything. Needs either an `ETag`/`If-None-Match` response or a metadata-only
  variant. **This is the one piece that requires coordinating on the site repo** — we are a
  contributor there, not the owner, so it is not ours to decide unilaterally.
- **It forces a full reindex.** `ServerBuildIdentity` SHA-256s the bundled API files into the
  cache identity, deliberately, so analysis can never survive an API change. Overriding them
  changes that identity, wiping the SQLite cache and triggering a cold index (~5.5 s on 1,105
  files). Correct behaviour, but it should be a conscious trade rather than a surprise.
- **Validate before replacing.** A truncated or empty 200 must not wipe the builtin library:
  parse it, require a `revision` newer than the bundled one and a non-empty `api[]`, and only
  then swap.

**It buys nothing today** — the bundled data is identical to the site's. The value is entirely
future-facing, for when the library improves between extension releases.

### 2. Headless CLI (`GSCode.Cli`)

The plan's original P13: `gscode check <folder>` (workspace-only resolver, full diagnostics,
non-zero exit on errors) and `gscode format --check|--write`, packaged as a dotnet tool for
mod-project CI. Cheap to build because the layering already isolates OmniSharp in
`GSCode.Server`, so Workspace + Parser are a complete LSP-free engine. Ships only if wanted.

---

## Decided — not doing

### Corpus grammar gaps (2 of the 3 found)

The corpus run over real `share\raw` found 4 failing files out of 980. One — `&"..."` parsing
as address-of instead of an istring — was fixed, because it also broke the spaced form
`& "loc"` in ordinary hand-written code. The other two are **deliberately left**: the game has
shipped, so these stock files are frozen, and neither pattern justifies a grammar change.
Diagnosis kept only because it was already done:

- **`gib.gsc(58)` / `gib.csc(35)`** — `#define GET_GIB_BUNDLES struct::get_script_bundles(...)`
  is object-like, but the call site writes `GET_GIB_BUNDLES()`, so expansion yields a call
  applied to a call result. Would be fixed by letting `ParsePostfixChain` accept `(` alongside
  `[` and `.`.
- **`vehicle_shared.gsc(3932)`** — an apparently unmatched `#/`. The dev-block markers look
  genuinely unbalanced in Treyarch's file, meaning our diagnostic may simply be correct. **Do
  not loosen the parser to accept unbalanced markers** without first confirming which side is
  wrong; that would trade a correct error for silent mis-parsing.

The corpus test prints both on every local run, so neither can be quietly forgotten.
