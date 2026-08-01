# Post-rewrite follow-ups

**P0–P14 are complete.** This file holds only what still needs a decision. Anything finished
has been removed — its record lives in the git history, and its outcomes are documented where they
belong: `PERF.md` (measured budgets, the cold/warm memory answer, the corpus category),
`GAME_PROFILES.md` (what each dialect claims and the evidence for it), `ARCHITECTURE.md` (structure
and the per-project `FOLDER.md` convention), and each project's own `FOLDER.md`.

A lesson worth keeping belongs in a comment beside the code it constrains, not here. This file is a
worklist; when its last entry goes, so does it.

---

## Backlog

### Three probable CSC builtins are missing from the BO3 library

The corpus sweep reports `error`, `add_object` and `warning` as unknown builtins in BO3's
`scene_shared.csc`/`.gsc`. Almost certainly real client-script builtins absent from
`t7_api_csc.json`; confirm against the reference library before adding.

Add them by HAND rather than regenerating: a regeneration rewrites fifteen files and invalidates
every workspace cache through `ServerBuildIdentity`, which is not worth it for three names.

### WaW and BO1 ship no stock-script list

`BundledDataFileNames` promises `waw_stock_scripts.txt` and `bo1_stock_scripts.txt` and neither
exists, so the warning about editing a file the game shipped never fires for those two games. It
degrades rather than breaks — an absent list reads as "nothing is stock" — which is why it survived
unnoticed. `BundledDataTests` now pins the "everything promised is shipped" invariant and carries
these two as the only documented exceptions; the exclusion list is itself asserted to shrink when
they arrive, so this cannot quietly become permanent.

Generating them means enumerating each game's raw tree, as `cod4_stock_scripts.txt` and
`t7_stock_scripts.txt` were. Needs `GSCODE_CORPUS_WAW` / `GSCODE_CORPUS_BO1` on the machine doing it.

### `5014 BuiltinFunctionNotFound` cannot tell a typo from a missing builtin

An unqualified call that nothing explains is reported as `5014`, and the rule cannot say which of
the two it is: a misspelling, or a real engine function our library lacks. Frequency is the only
discriminator, and only `BuiltinHarvestTests` applies it.

If `5014` proves noisy on real mod code, the answer is a better library rather than a weaker rule
— see the harvest reports under `tests/GSCode.Server.Tests/harvest/`. (The lint's own reasoning,
and why it is an Error, lives in comments on `FunctionResolutionLint`.)

### `gscode-5000 NamespaceNotImported` should be an Error, not a Warning

Calling into a namespace no `#using` imports fails to LINK at runtime — the script does not load.
That is a broken build, not a matter of style, and the severity should say so.

Left as a follow-up rather than flipped immediately because the lint has only just stopped
misfiring: it reported 23 false positives on class-method calls until `NamespaceUsageLint` learned
to skip class qualifiers, and it now reports zero across the stock corpus. Give it some real-world
use at Warning first; promoting a rule to Error the same day its false positives were fixed is how
you end up with red squiggles on working code.

`CorpusDiagnosticSweepTests.NoNamespaceIsReportedUnimported` asserts the zero, so a regression
shows up before the promotion does.

### Cross-file lints for files that are not open

`gscode.diagnostics.scope` now publishes problems for indexed files, but a closed file reports
only what `ScriptRecord.Diagnostics` holds — the parse-level findings (syntax errors, unknown
directives, precache mistakes). Opening it adds the cross-file lints: unused `#using`, namespace
usage, private access, dev-block calls, read-only writes, prefer-boolean-literal.

So a file can gain problems on being opened, which is honest but slightly odd.

Closing the gap needs those lints run over the whole workspace, and they need a `ParseResult`,
which records deliberately do not retain — holding 1,105 of them is exactly the memory the
rewrite avoided. Options, roughly in order of appeal:

1. A background pass after indexing that re-analyses each file, runs the lints, stores the merged
   diagnostics on the record and drops the result. Costs a second analysis pass but bounded, and
   it can be cancelled and resumed.
2. Run the lints during indexing's second phase, once the database is complete enough for the
   cross-file ones to be meaningful.
3. Leave it, and document that closed files report parse-level problems only.

Whichever, the count in the status bar and the Problems panel should agree, so decide before
adding any "N problems" summary.

**Revisited, and deliberately still not done.** The OPEN half of this is now solved — an edit that
changes what other files can see republishes their diagnostics (`ExportSignature` +
`DependentDiagnosticsRefresher`). That fix is cheap for exactly two reasons, and it is worth being
precise about them because NEITHER holds for closed files:

* open documents are the user's tabs, so there are a handful of them; and
* their text has not changed, so the parse is reused and only the lint pass re-runs.

Closed files are the opposite on both counts: there are thousands, and none has a retained parse.
Measured against the corpus sweep, which does precisely this work, a parse-plus-all-lints pass runs
at roughly 44 ms/file — about 43 s for BO3's 980 stock scripts, or a second or two for a mod of
fifty.

The trap is that option 1 reads like a ONE-OFF cost and is not. Stored lint results go stale on the
same trigger the open files do: rename a function and every stored diagnostic that mentions it is
wrong. So it is a sweep per rename, not a sweep per session — seconds of background CPU on a common
keystroke, which is a louder problem than the quiet gap it closes.

Doing it properly therefore needs incremental invalidation, not a re-sweep: a reverse-dependency
index answering "which files reach this one", so only genuinely affected files re-lint. That index
is the hard part, and the difficulty is documented rather than assumed — under the merge dialects an
unqualified call resolves by NAME across the whole workspace, so a narrow answer is wrong rather
than merely conservative (see the same problem in `DatabaseQueries.ScopeToIncludeGraph`).

That is a subsystem, not a bolt-on, so it stays here until it is worth one. What DID land meanwhile:
an on-disk change now republishes closed files' stored diagnostics (`WatchedFilesHandler` calls
`WorkspaceDiagnosticsPublisher.Refresh()`), so what closed files do report is at least no longer
stale after a branch switch.

---

## Known limitations from the triage pass

Recorded because each was a deliberate stopping point, not an oversight. The triage plan lives
at `~/.claude/plans/i-m-looking-into-recreating-linear-raccoon.md`; P0, P1 and the hover/doc
half of P2 are done.

### Type hover across branches reports the last arm, not the join

`FlowTyper.TryGetLocalTypeAt` now takes the last assignment at or before the cursor, which is
exact for straight-line code and fixed the reported "reassigned variable still says int" bug.
Across `if`/`else` it reports whichever arm is written last rather than `Join` of both:

```gsc
if ( c ) { x = 1; } else { x = "s"; }
use( x );   // reports string; the truth is int|string
```

The join machinery exists (`ScrType.Join`, already wired into the walk) but the walk does not
retain its environment per position, so the hover has nothing to sample. Fixing it properly
means recording the environment at statement boundaries — worth doing only if the wrong answer
here turns out to bite in practice, since both the old and new behaviour are wrong in this case
and the new one is right far more often.

### Parameters and fields still get no inferred type on hover

Only locals with a typed assignment are covered. A parameter has no assignment to read, and
`self.foo` shows the engine's field data but never an inferred type. Both are additive on top
of the current lookup.

### `#using` is treated as non-transitive for class completion

`DatabaseQueries.AllVisibleClasses` offers classes from the asking file and the files it
`#using`s directly. Measured against the corpus first: all 8 cross-file class uses in the 980
stock scripts name the declaring file in their own `#using` list, so nothing real depends on an
import chain. If a mod turns out to rely on transitivity, this is the place to widen.
`LookupClasses` is deliberately left unfiltered so go-to-definition still works on a class
written without its import.

### ScriptDoc coverage is 499 of 572 blocks

`ScriptDocCorpusTests` asserts a floor rather than an exact figure, since the corpus is whatever
mod-tools version is installed. The ~70 unaccounted blocks are most likely attached to classes
or sitting in positions `FindDocComment`'s two-line window does not reach; nobody has checked
which. Worth a look only if a real file shows a missing hover.

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
(`entry.DevOnly ?? DevOnlyBuiltins.Contains(name, game)`) and nothing else changes.

**The list is now PER GAME** — `DevOnlyBuiltins` is keyed by short name, with entries for `bo3`
and `cod4`. So curating this for another game is two steps, not one: count that game's own scripts
inside versus outside `/# #/`, then add its entry. Skipping the count is not a shortcut but the bug
the keying exists to prevent — BO3's list applied to CoD4 called `println` dev-only and reported 598
Errors on shipped code, where the same count returns 220 inside against 438 outside. CoD4's entry is
deliberately EMPTY rather than absent: sixteen of BO3's twenty names are never called there and the
other four are called outside dev blocks, so "measured, and the answer is none" is recorded as the
fact it is.

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

### Formatter line wrapping

The formatter does not wrap long lines and will not. Everything else the formatter follow-up once
listed has shipped — `padParens`, `maxBlankLines`, per-request `tabSize`/`insertSpaces`, consecutive
alignment (`alignConsecutive`), directive sorting, and on-type formatting scoped to the alignment
group around the cursor with `editor.formatOnType` on by default.

Measured before deciding, across 390,434 BO3 lines and 335,608 CoD4 lines: the 95th percentile is 82
and 85 characters, and only 1.3%/1.4% pass 120. The number that decided it is not the tail's size
but its MEANING — these scripts never wrap, which is exactly why the long lines are long. There is
no convention here to conform to, so wrapping would be INVENTED rather than discovered, and the
formatter's whole premise is that it encodes what the corpus already does. This is the same test
that kept `braceStyle` out (51,048 Allman braces against 37 same-line), reaching the opposite
verdict for the same reason.

Revisit only if that premise changes — if mod authors turn out to wrap by hand, the measurement will
say so and this should be re-run. Whoever does will need to settle where a break is allowed
(argument lists, `&&`/`||` chains and `+` concatenations are the three shapes long enough to
matter), what the continuation indent is, and whether the `TokenStreamMatches` corruption guard
still holds once one line becomes several.

### Corpus diagnostic sweep — nothing outstanding

`CorpusDiagnosticSweepTests` runs the editor's whole lint pipeline over the shipped scripts. Since
those shipped, anything it reports is either a real defect in Treyarch's code or a false positive in
ours. Both groups it still reports have now been chased to the end, and neither is ours.

Worth recording how the one real false positive was missed for a while: this entry originally read
"nothing outstanding" on the strength of the BO3 numbers alone, while the SAME code reported 598
`5006` Errors across 107 CoD4 files. The sweep prints per game and the conclusion was drawn from one
of them. Cause and fix are under `DevOnlyBuiltins` — a BO3-measured table was being applied to every
game — and the lesson generalises past that one list: a claim about "the corpus" is a claim about
whichever game was actually looked at.

The same lesson paid out twice. Correcting CoD4 left WaW reporting **972 `5006` Errors across 184
files** from the identical cause, unnoticed for the same reason — and the trade-off had been named at
the time the correction was written in CoD4's own data rather than keyed per game. It cost nothing to
fix: the generator's inheritance copies CoD4's entry verbatim, so regenerating carried `devOnly:
false` to WaW and BO1 and took both to zero. Counted on WaW's own scripts, `PrintLn` is 479 calls
inside a dev block against 954 outside, `Line` 104:157, `Print3d` 93:118 — the same inversion of BO3's
269:2 that made CoD4 wrong. `SetDebugSideSwitch` (1:0) is the one name that stays dev-only there.

**`gscode-5006 DevOnlyFunctionCalledFromRelease` — 6 Errors, all GENUINE.** Checked site by site
against the BO3 corpus; the standing suspicion that the callers were themselves dev-only is wrong,
and no change to `DevBlockCallLint` is warranted:

- `util::error` (×3, `_globallogic_audio.gsc:225` and `:496`, `_zm_weapons.csc:134`) — declared
  inside a `/#` at `scripts\zm\_util.gsc:15`, and every call site is an ordinary `else if` branch.
  There is no non-dev `error` in namespace `util` for GSC to fall back to; the one in
  `util_shared.csc` is client-side only.
- `debug_spherical_cone` (`_microwave_turret.gsc:467`) — dev-only in `util_shared`, called from
  release code in another file.
- `printHashIDs` (`_zm.gsc:419`) — declared inside a `/#` at `_zm.gsc:7136`. Worth knowing that a
  naive delimiter count says otherwise: the only `/#` before the call is on line 47, inside
  `//#using scripts\zm\_zm_hero_weapon;`, where the comment slashes abut the directive's hash. The
  lexer is right and the eyeball is wrong.
- `Print3d` (`vehicle_shared.gsc:3929`) — the interesting one. `show_node_debug_info` and
  `print_debug_info` are plainly MEANT to be dev-guarded: there is a closing `#/` on line 3932. But
  no `/#` opens it — the nearest one, at 3287, is closed at 3292 — so the guard never begins and the
  functions really are release code calling a dev-only builtin. A stray delimiter in the stock
  scripts, surfaced by the lint doing its job.

**`UnusedUsing` (2,187 at last sweep)** — also real: a text scan for the imported file's namespace,
with comments stripped, finds an actual `ns::` use in **zero** of them. Stock scripts simply carry a
lot of stale imports. It is a Hint, so they grey out rather than nag.

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
