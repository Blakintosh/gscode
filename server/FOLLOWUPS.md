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

### 2. Curate the dev-only builtin list

`Api/DevOnlyBuiltins.cs` drives the `DevOnlyFunctionCalledFromRelease` diagnostic for engine
builtins. **The plumbing is done** — `BuiltinFunction.IsDevOnly` carries the flag, `ApiLoader`
stamps it, and the lint reads that one property — so this is purely a data-curation task. When
the API data eventually carries its own `devOnly` field the loader already prefers it
(`entry.DevOnly ?? DevOnlyBuiltins.Contains(name)`) and nothing else changes.

**Why a hand-curated list rather than derived data:** neither available source is accurate.

- `bo3_scriptapifunctions.htm` (Treyarch's own docs) marks only 3 functions — `Print`,
  `PrintLn`, `SetAnimForceNew` — and omits the debug-draw family entirely: `Line`, `Sphere`,
  `Print3D`, `Box`, `Circle`, `DebugStar` and the `Record*` functions appear in none of its
  2,327 entries. It also has no Debug category; its categories are Player, AI, Vehicle, Gfx,
  Utility, Math, UI and Weapon.
- `t7_api_*.json` (community-augmented) does document that family and describes them
  consistently as debug instruments, but its generation dropped the "Development only" prefix
  that the HTM carries for `PrintLn`, leaving it described as merely "Writes a line to the
  console". So it cannot be trusted alone either.

**Current list, and how it was arrived at.** Candidates came from both sources, then each was
validated against ~980 stock scripts by counting calls inside versus outside `/# #/`:

- Kept, only ever called inside dev blocks: `Line` (67:0), `Record3DText` (71:0), `DebugStar`
  (33:0), `Circle` (29:0), `Sphere` (23:0), `RecordSphere` (22:0), `Box` (12:0), `RecordStar`
  (8:0), `PrintTopRightln` (6:0), `RecordEntText` (6:0), `SetDebugSideSwitch` (1:0),
  `SphericalCone` (1:0).
- Kept, overwhelmingly inside: `PrintLn` (269:2), `Print` (41:2), `Print3d` (26:1). The handful
  outside are most likely stock bugs — this corpus also ships an unbalanced `#/`.
- Kept on family grounds, unused in stock so no evidence either way: `LineList`, `DebugBreak`,
  `RecordCone`, `RecordEnt`, `SetAnimForceNew`.
- **Rejected despite calling themselves debug instruments**, because stock code calls them
  outside dev blocks and never inside: `PixMarker` (0:2), `InfoVolumeDebugInit` (0:1). Also
  left out: `GetDebugEye`, an ambiguous getter with no usages either way.

**What is left to do:** prune and extend by hand as real usage turns up. The diagnostic is
Error severity, so a wrong entry flags working code — validate a candidate against the corpus
before adding it. `DevBlockCallLintTests.CandidatesContradictedByStockCode_AreExcluded` pins
the two rejections so nobody re-adds them from the description alone.

### 3. Headless CLI (`GSCode.Cli`)

The plan's original P13: `gscode check <folder>` (workspace-only resolver, full diagnostics,
non-zero exit on errors) and `gscode format --check|--write`, packaged as a dotnet tool for
mod-project CI. Cheap to build because the layering already isolates OmniSharp in
`GSCode.Server`, so Workspace + Parser are a complete LSP-free engine. Ships only if wanted.

---

## Decided — not doing

### Corpus grammar gaps (1 of the 3 found)

The corpus run over real `share\raw` found 4 failing files out of 980. One — `&"..."` parsing
as address-of instead of an istring — was fixed, because it also broke the spaced form
`& "loc"` in ordinary hand-written code. The other two are **deliberately left**: the game has
shipped, so these stock files are frozen, and neither pattern justifies a grammar change.
Diagnosis kept only because it was already done:

- **`gib.gsc(58)` / `gib.csc(35)`** — `#define GET_GIB_BUNDLES struct::get_script_bundles(...)`
  is object-like, but the call site writes `GET_GIB_BUNDLES()`, so expansion yields a call
  applied to a call result. Would be fixed by letting `ParsePostfixChain` accept `(` alongside
  `[` and `.`.
The corpus test prints it on every local run, so it cannot be quietly forgotten.
