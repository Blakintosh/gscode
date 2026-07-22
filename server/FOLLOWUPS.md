# Post-rewrite follow-ups

**P0–P14 are complete.** This file holds only what still needs a decision. Anything finished
has been removed — its record lives in the git history, and its outcomes are documented where
they belong: `tests/PORTING.md` (every v1 test class resolved), `PERF.md` (measured budgets,
the cold/warm memory answer, the corpus category), and `ARCHITECTURE.md` (structure and the
per-project `FOLDER.md` convention).

---

## Backlog

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

### Gate every completion context on where the cursor actually is

**Confirmed:** completing at `self notify(#` offers all 11 directives — `#using`, `#insert`,
`#namespace`, … — inside a call argument, where the only thing a `#` can begin is a hash string.

The cause is that `IsAfterDirectiveHash` (`CompletionEngine`) asks only "is there a `#` before the
word being typed", never "may a directive appear here at all". Directives are top-level only, so
the check needs to fail inside a function body — `IsInsideFunctionBody` already answers that, it
is simply consulted after the directive branch has already returned. What SHOULD be offered there
is the known hash strings, inserting `"name"` onto the `#` already in the buffer, mirroring how
the `#precache` asset-type slot works.

**Treat this as the general problem it is, not one fix.** Every context in `Complete` is detected
by looking BACKWARD for a trigger, and none of them confirm that the construct is legal at that
position. Worth an audit of each, since the same shape is likely elsewhere:

| Context | Trigger | Where it is actually legal |
|---|---|---|
| Directives | a `#` before the word | Top level only, never in a function body |
| `#using`/`#insert` paths | the directive earlier on the line | Top level only |
| `#precache` asset types | inside the first string argument | Only in the real directive |
| `ns::` functions | a `::` behind the cursor | Anywhere in an expression |
| `owner.` fields | a `.` behind the cursor | Anywhere in an expression |
| Literals | the cursor inside a string token | Anywhere a string is legal |

The last three are fine as they are. The first three are all top-level constructs being offered
from anywhere, so a single "is a directive legal at this position" helper probably covers them,
rather than three separate guards.

A test per row would pin the whole class rather than the one symptom.

---

### Auto-complete the call punctuation — `gscode.completion.callPunctuation`

Accepting a function completion in statement position should leave
`self foobar( <cursor> );` rather than `self foobar()` with the semicolon still to type.

**What already exists.** `(` → `)` is an auto-closing pair
(`client/language-configuration.json`), function and builtin completions already insert
`name($0)` so the parentheses and cursor placement are done, and on-type formatting is already
registered on `;` and `}` (`DocumentOnTypeFormattingHandler`). The missing pieces are the
semicolon and, more importantly, not fighting the user for it.

**The real problem: typing `);` is muscle memory.** Insert punctuation the user then types
again and you get `foobar());` or `foobar();;`. Worse than not helping. Two different mechanisms
are needed, because the two characters behave differently:

- **`)` — the editor can already handle this.** `editor.autoClosingOvertype` makes typing a
  closer move over an identical one instead of inserting. The default `"auto"` only tracks
  closers the EDITOR inserted, which will not cover a `)` that arrived inside a completion
  snippet, so this wants `"always"` in `configurationDefaults` for `[gsc]`/`[csc]`/`[gsh]`.
  Overtype only fires when the very next character is the same closer, which is exactly when a
  second one would be wrong, so `"always"` is safe here.
- **`;` — needs our own handling.** There is no built-in overtype for it. The on-type formatting
  handler already fires on `;`, so it can return an edit deleting the duplicate when the
  character after the cursor is also `;`. **Guard `for ( ;; )`**, where a doubled semicolon is
  the language, not a mistake — check that the enclosing construct is not a `for` header rather
  than pattern-matching the text.

**Only in statement position.** `x = foobar()` and `foobar()[0]` must not gain a semicolon. The
completion engine already distinguishes statement scope from expression context
(`IsInsideFunctionBody` plus the trigger token), so the entry can carry the semicolon or not
rather than the handler guessing afterwards.

**Setting shape.** `off` | `parens` | `parensAndSemicolon`. Worth defaulting to `parens` — the
current behaviour — so the semicolon is opt-in until the overtype handling has been used in
anger. Whether it should eventually default on is the open question; decide after trying it.

---

## Deferred by request

### Formatter — a further pass is planned

Left where it is on purpose: another round of formatter changes is coming, so anything more here
would be rework. What landed so far is the settings layer, not the style itself:

- `tabSize`/`insertSpaces` are honoured per request, and the GSC languages default to tabs in
  `client/package.json`'s `configurationDefaults` (247,613 tab-led indented lines across the stock
  scripts against 886 space-led).
- `gscode.format.padParens` and `gscode.format.maxBlankLines` exist; `maxBlankLines` defaults to 2,
  which is what the doc comment always claimed while the code capped at 1.
- `braceStyle` was deliberately NOT added — 51,048 Allman braces against 37 same-line makes it a
  convention rather than a preference. Revisit only if that measurement is disputed.

Still open whenever the pass happens: line wrapping (there is none — long argument lists stay on
one line), alignment of consecutive assignments, and whether `#insert`ed regions should be
reformatted at all.

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
