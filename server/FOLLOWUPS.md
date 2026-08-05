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

### `5014 BuiltinFunctionNotFound` cannot tell a typo from a missing builtin

An unqualified call that nothing explains is reported as `5014`, and the rule cannot say which of
the two it is: a misspelling, or a real engine function our library lacks. Frequency is the only
discriminator, and only `BuiltinHarvestTests` applies it.

If `5014` proves noisy on real mod code, the answer is a better library rather than a weaker rule
— see the harvest reports under `tests/GSCode.Server.Tests/harvest/`. (The lint's own reasoning,
and why it is an Error, lives in comments on `FunctionResolutionLint`.)

### `5025 KeywordNotInDialect` only reaches the call-shaped half

`5025` explains a word that a later game has as a keyword but this dialect does not — `foreach` under
CoD4 being the case it was written for. It is raised as the last branch of `FunctionResolutionLint`,
which means it only ever sees words that reached the lint AS A CALL: the lexer left the word an
identifier, the parser read identifier-then-`(` as a call, and the lint found nothing of that name.

That covers `foreach`, and the BO3 intrinsics written call-shaped (`waitrealtime`, `vectorscale`,
`profilestart`, `profilestop`). It does not cover the keywords that open a STATEMENT or a
DECLARATION, because those never form a call and so never arrive:

| shape | absent in | what the user gets today |
|---|---|---|
| `do { … } while ( x );` | everything before BO3 | a parse error on the block |
| `class Foo { }`, `function foo()`, `const X = 4;` | everything before BO3 | a parse error |
| `new Foo()` | everything before BO3 | a parse error |
| `childthread foo();`, `call [[ ptr ]]()` | CoD4/WaW/BO1/BO3 | a parse error |
| `in`, as the `foreach` separator | before MW2 / BO3 | swallowed into the failing arg list |

The parse error is not wrong — the text genuinely is not grammatical there — it just never says the
one thing worth saying, which is that the construct belongs to a different game.

Three reasons this is a follow-up rather than a second branch:

1. **The parser already speaks first.** A statement that fails to parse reports a 3xxx, and adding
   `5025` beside it puts two diagnostics on one range for one mistake. The rule has to REPLACE the
   parse error, which means the decision belongs inside the parser's statement dispatch — where an
   identifier in statement position could be checked against `GameProfile.EarliestWithKeyword`
   before the generic "unexpected token" is raised.
2. **A bare token scan is not enough.** Without syntactic position, `call` and `vararg` false-positive
   immediately: BO3's own stock scripts use `call` as an ordinary variable ~69 times, which is the
   very reason the keyword set gates it. Whatever does this has to know it is looking at a statement
   opener, not any occurrence of the word.
3. **The band is wrong for a parser rule.** `5025` sits in the 5xxx workspace band because that is
   where it is raised from now. A parser-raised version wants 3xxx by the convention in
   `add-diagnostic`. Decide whether the code moves, whether one code is legitimately raised from two
   layers, or whether the parser gets its own — before writing either.

Not urgent: every one of these shapes already stops the user. The gap is the explanation, not the
detection.

### `LookupFunctions` scans the whole store, once per call site

The cross-file lints cost roughly twenty times the parse they run on top of, and four rules are
97% of it. Measured with `CorpusPerfTests.WorkspaceLints_WhereTheTimeGoes`, built with
`-p:GscodeInstrumentation=true` so the per-lint scopes in `WorkspaceLints` record:

| | BO3, 980 files | CoD4, 894 files |
|---|---|---|
| `FunctionResolutionLint` | 6.6 s | 6.8 s |
| `ArgumentCountLint` | 6.3 s | 4.2 s |
| `DevBlockCallLint` | 5.7 s | 7.9 s |
| `PrivateAccessLint` | 2.7 s | 4.8 s |
| everything else combined | ~0.6 s | ~0.8 s |
| **total** | **21.9 s** | **24.5 s** |

Read the shares, not the absolute milliseconds: instrumentation costs something itself, and this is
the sequential lint sweep rather than the parallel corpus one.

What the four share is `DatabaseQueries.LookupFunctions`, which walks `store.AllRecords` and every
function in each — around thirty thousand symbols on BO3 — and is called once per CALL SITE. A file
with two hundred calls scans the whole store two hundred times. Only `IncludeUsageLint` caches by
name; the four expensive ones do not.

Two fixes, and they are not alternatives:

1. **A name-to-declarations index on the store**, built once at index time, so a lookup is a
   dictionary hit rather than a thirty-thousand-symbol walk. Surgical inside `LookupFunctions` —
   the shadowing and privacy filtering apply unchanged afterwards and no caller moves — and it
   speeds up hover, completion, definition and references too, not only the lints.
2. **A per-file cache by name** in the four rules, which is what `IncludeUsageLint` already does.
   Cheaper to write and helps a file that calls the same name repeatedly, but it does nothing for a
   file whose calls are all distinct. Worth having anyway; not a substitute for (1).

Why this matters beyond a sweep: `TextSyncHandler` debounces at 250 ms, and the tail is worse than
the medians suggest — p90 64 ms and p99 197 ms on BO3, p90 68 ms and p99 343 ms on CoD4, with
`scoutsniper.gsc` at 611 ms. The slowest files cannot keep up with typing.

Two things this is NOT. It is not a regression from the 5026 work: `IncludeUsageLint` is 0.12 s on
CoD4 and unmeasurable on BO3, and `FileImports.Resolve` is 59 ms / 29 ms. And there is no before
figure for the layer at all, because nothing measured it until now — so treat these as the baseline
rather than as evidence of anything having got worse.

### The TextMate grammar colours every dialect's keywords in every dialect

`gsc.tmGrammar.json`'s `control` rule is the UNION of all five games' keywords, because a grammar
runs before the server is asked and cannot know which game is selected. So `foreach`, `class`,
`new`, `childthread` and `call` render as keywords while editing CoD4, which has none of them.

The union is the right default — under-highlighting is worse, and picking one game's set would
leave `#include` and the profiler pair plain in the four Infinity Ward games. But the comment on
that rule used to justify the cost by saying the server owned the accurate verdict "through
semantic tokens and gscode-1004", and neither half is true: `1004` is `UnknownDirective` and covers
directives only, and `SemanticTokensHandler` stopped emitting Keyword tokens (its legend keeps the
slot). The comment now says what actually holds — `5025` names the game a keyword belongs to once
the word is USED, which is a diagnostic, not a colour.

Colour cannot be fixed from the server at all: semantic tokens can add or override a scope, never
withdraw one, so there is no token that un-highlights a word TextMate already matched. The only
real fix is per-dialect grammars — five `gsc.<game>.tmGrammar.json` files differing in one regex,
selected by, at best, a language id per game, since `contributes.grammars` cannot be switched on a
setting either.

That is a lot of duplication for a colour, and it would fragment the language id that `.gsc` files
resolve to, which every other contribution point keys off. Not worth doing until someone actually
reports being misled by it — the diagnostic now tells them, which is the half that matters.

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

Recorded because each was a deliberate stopping point, not an oversight. P0, P1 and the
hover/doc half of P2 are done; the remaining items below are the decisions still worth making.

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

### 1. `apiUpdate.ts` — proposed opt-in online refresh of the builtin API

Fetch a newer builtin-function library from gscode.net instead of waiting for an extension
release. This is not implemented yet: `gscode.apiUpdate.enabled` is a proposed setting, not a
currently supported configuration key. The bundled JSON remains the fallback.

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
the API data carries its own `devOnly` field the loader prefers it; otherwise it falls back to
`DevOnlyBuiltins.Contains(name)` and nothing else changes.

**The fallback list is intentionally global** — `DevOnlyBuiltins` is keyed by short name because
the generated API data is the place for game-specific corrections. A game whose API data states
the answer wins; only a game that states nothing lands on this fallback. That keeps the curated
table small while making the precedence explicit.

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
