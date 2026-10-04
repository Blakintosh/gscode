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

### Variadic builtins are not modelled, so a builtin call has no upper argument bound

`ArgumentCountLint` treats a builtin's mandatory count as a LOWER bound and stops there. The upper
bound was written, measured and withdrawn: it reported 634 Errors across 134 shipped BO3 scripts,
and every one was the library under-declaring rather than the script over-calling — `Array( a, b, c )`
is variadic against a single declared parameter, `Record3DText` takes six against one. The full
reasoning sits on `InspectBuiltin`; this is the entry `ARCHITECTURE.md` points at for it.

**The data has since grown the marker the check needs.** A parameter's type is structured in the
bundled JSON (`"type": { "dataType": …, "isArray": … }`) and `vararg` is one of its spellings — 34 of
BO3's 2,191 GSC entries carry it and 15 of its 803 CSC ones, both names above among them.

**The marker is carried.** `ApiLoader` passes `IsVararg(parameter.Type)` into
`BuiltinParameter.IsVariadic`. It has no production reader — only `ApiTypeParsingTests` reads it,
and `ArgumentCountLint` still consults only `Mandatory` — so what is left is the per-game
measurement, not the plumbing.

**Carrying the marker is the easy half; coverage is the whole problem.** The bound is only worth
having where `HasReliableBuiltinSignatures` holds, which is CoD4 and BO3 — and CoD4's 819 entries
mark no vararg at all, while BO1 marks 10, WaW 2 and MW2 none. So on one of the two eligible games
the marker is certainly incomplete and on the other it is merely untested, and an upper bound shipped
on a marker that is mostly-there is the 634 again.

Route, in order:

1. Re-run the upper-bound check per game with vararg parameters exempt, and read the TOP REPORTED
   NAMES rather than the count: a shape shared across them is another library gap, not a user
   mistake. `tests/GSCode.Server.Tests/harvest/*_client_arity.json` is already this measurement in
   miniature — it records `declaredMax` against observed counts (`PlaySound`: 1 declared, 2–4
   observed across 190 calls).
2. Ship it per game only where the remainder is zero, the way `HasReliableBuiltinSignatures` was
   earned in the first place.

If a game's remainder will not reach zero, the data already holds the discriminator for a weaker
severity rather than none: `BuiltinFunction.Confidence` (loaded, not yet read). The JSON's per-entry
`flags` (BO3's GSC library: 157 `verified` against 2,032 `processed`; BO1's and MW2's carry 259 and
264 `aiGenerated`) are still dropped by the loader. The same split is what `ArgumentTypeMismatch`
needs, in the entry below.

### 1.5's type-derived diagnostics: ten restored, four ruled out, six blocked

1.5 ran an abstract-interpretation pass (`CFA/ControlFlowAnalyser` then `DFA/TypeFlowAnalyser`, live
on every analysis — `v1.5.0:server/GSCode.Parser/Script/Script.cs:315-321`) over a flag lattice, and
raised twenty-one codes from it and its neighbours that the rewrite dropped as a family. The rewrite's
rule for bringing them back: one at a time, each swept over all five corpora before it gets a
severity, because the family restored at once is what 1.5 shipped and then had to hold back with
false-positive regression tests. This tree is at parity with 1.5 or ahead of it in every other
diagnostic layer.

The type model it needed now exists: `ScrValue` (Core/Symbols) is a union lattice with disjoint bits,
folded constants and tri-state truthiness — 1.5 tracked only `bool? BooleanValue` and folded nothing —
and `FlowTyper.InferValues` gives the value of every expression. The builtin library is on the lattice
too: `ApiLoader.ParseType` puts each parameter's declared type on `BuiltinParameter.Types`, and
`ApiLoader.ParseConfidence` keeps each entry's `high`/`medium`/`low` on `BuiltinFunction.Confidence`.
Neither of those two has a reader yet; both exist for `ArgumentTypeMismatch` below.

| status | codes |
|---|---|
| **Restored, no types needed (8)** | `2017`/`2018` duplicate macro and parameter · `5027` second `default:` · `5028` using a threaded call's result (1.5's `ConsumedThreadedCallResult` and `AssignOnThreadedFunction`, one mistake counted twice) · `5029`/`5030` the `const` pair · `5031` literal-zero divisor · `5032` statement with no effect |
| **Restored on the lattice (2)** | `5033` enumerating a non-array · `5034` a vector component that cannot be a number |
| **Ruled out (4)** | `DoesNotContainMember`, `NoImplicitConversionExists`, `OperatorNotSupportedOnTypes`, the type half of `UnreachableCase` — see the end of this entry |
| **Blocked (6)** | `ArgumentTypeMismatch` (+ `Unverified`), `PredefinedFieldTypeMismatch`, `CannotUseAsIndexer`, `ExpectedFunction`, `StoreFunctionAsPointer`, `CannotAssignToImmutableEntity` |

Every restored code reports zero on all five corpora except `5028`, whose 172 are genuine. `5033` and
`5034` report zero everywhere, so their tests carry controls that must fire.

**The blockers, and what unblocks each:**

| code | blocker | unblocked by |
|---|---|---|
| `ArgumentTypeMismatch` | 211 findings on bo3 and 17 on cod4, none real (2026-09-28) | game-data fixes, below |
| `PredefinedFieldTypeMismatch` | 46 findings on bo3, none real | the object-field fixes, below |
| `CannotUseAsIndexer` | `FlowTyper.TypeOf` returns a value for an `IndexNode` without typing the index expression | typing the index; additive |
| `ExpectedFunction` | nothing types the operand of `[[ x ]]()` | typing a pointer dereference's operand |
| `StoreFunctionAsPointer` | a resolution question, not a type one; `5016` already reports the same identifier | replacing `5016` on that range rather than stacking a second diagnostic |
| `CannotAssignToImmutableEntity` | `ObjectField` has a per-field `ReadOnly` and nothing marks a whole entity kind immutable | a data-model addition |

**`ArgumentTypeMismatch` is the most valuable of the six and the one closest to shipping** — its
plumbing is finished, and `BuiltinFunction.Confidence` is exactly where 1.5's `Unverified` twin gets
its severity split, without a second code. An exploratory sweep (not kept) reported an argument only
when no type the flow allows is accepted by any overload with a parameter at that position, after the
obvious coercions (numbers and bools interchange, the three string kinds interchange, `undefined` is
always allowed); unqualified calls outside classes only.

| game | builtin calls | findings | real, of those sampled |
|---|---|---|---|
| cod4 | 39,426 | 17 | 0 |
| bo3 | 31,640 | 211 | 0 |

Every finding sampled had one of three causes, and they are the route, in this order:

1. **The engine coerces scalars to string and the data does not say so** — about 160 of bo3's 211:
   `SetDvar( "ui_guncycle", 0 )` (78), `assert( cond, arr.size )`, `profilelog_endtiming( 4, ... )`.
   One decision, not a data edit: accepting scalar-to-string leaves roughly 47 on bo3.
2. **The data is wrong.** This list is the worklist for `tools/field-data`:
   - `RecordSphere` parameter 4 is declared `bool`; the scripts pass `"Script"` (11).
   - `GetDvarInt`'s default is declared `int`; the scripts pass `"7"` (10).
   - `GetWeaponAmmoClip` declares `entity`; BO3 passes a weapon object (7), read from
     `dualWieldWeapon`, which is typed `string`.
   - `IsWeapon` declares `entity`, but it is a type test and takes anything (2).
   - `GroundTrace`'s ignore-entity parameter is declared `entity`; the scripts pass `false` (2).
   - Object fields: `team` is typed `int` and holds team strings (`GetPlayers( player.team )`,
     `self.team = self.sessionteam`); `type`, `attachments`, `horzalign`/`vertalign` (assigned
     `"user_right"` throughout `hud_util_shared.gsc`) and `combatmode` are the same shape. These are
     also every one of `PredefinedFieldTypeMismatch`'s 46.
   - CoD4: `AnimCustom` takes a function pointer, not a `string` (8); `SetGoalPos` takes a vector, not
     an `entity` (5); `CheckGrenadeThrowPos` argument 2 is the string `"min energy"`, not a vector (3).
3. **Method form is not distinguished**: `self spawn( origin, angles )` was judged against the global
   `Spawn( classname, ... )` (13). The rule has to match `call.Target` against `BuiltinOverload.CalledOn`.

Then re-sweep, read the top reported NAMES rather than the count, and ship per game only where the
remainder is zero — the way `HasReliableBuiltinSignatures` was earned. Treat any high count as a
library defect until proven otherwise: the mandatory-count check (141, 280 and 157 on CoD4, WaW and
BO1), the builtin upper bound (634 on BO3), `PredefinedFieldTypeMismatch` (46) and
`OperatorNotSupportedOnTypes` (752) all ended with the inference right and the data wrong.

**Traps the restorations found, for whoever writes the next one:**

- `ScrValue.IsUnknown` is exact equality with the universe. A value narrowed by `isdefined` is the
  universe minus undefined: no longer "unknown" by that test while still knowing nothing. A rule that
  guards on `IsUnknown` has this hole; `OperatorNotSupportedOnTypes` fell into it.
- Scope a name per function, not per file: `5030` collecting `const` names file-wide reported ten
  writes on BO3, every one an unrelated local sharing the name.
- Stand down on a file the parser could not read: `5032`'s first nine findings were all recovery
  wreckage after a parse error.
- The first argument-type sweep also found a typing bug — `"at " + self.origin` typed as a vector,
  because vector-with-scalar was decided before concatenation. Fixed in `2a4fd572`; a type rule is a
  good test of the typer.

**Ruled out permanently**, with reasons, so they are not revisited as oversights:

- `DoesNotContainMember` — unsound in GSC. Fields can be added to any entity or struct at runtime, so
  "does not contain" is never knowable. 1.5 shipped it as an Error and needed a false-positive
  regression test (`StringSizeAndBreakTests.cs:73`) to hold it back.
- `NoImplicitConversionExists` — GSC truthiness accepts nearly everything, so the broad form has no
  sound core to narrow down to. Unions did not change that.
- `OperatorNotSupportedOnTypes` — written against the union lattice, measured at 752 findings on
  shipped code, withdrawn. Half was the `IsUnknown` trap above; the other half is that `ScrOperators`
  rejects `vector + scalar`, which the stock scripts do throughout. The operator table is stricter than
  the engine — fine for typing, not a basis for a diagnostic — and nothing establishes what the engine
  actually does there.
- The type half of `UnreachableCase` — needs the switch subject and every label typed exactly, and a
  label is usually a macro or a bare literal while the subject is usually a parameter. The
  duplicate-label half ships as `5017` and the duplicate-`default:` half as `5027`.

### A macro can hide the end of an unbraced body from the formatter

The formatter ends an unbraced body at its `;` — that is where the body's indent is released and
where the blank line after the chain goes. It reads the raw tokens, before any macro is expanded,
so a macro invocation can hide the `;` or `}` it keys on. Nested unbraced bodies are legal and
must keep formatting as written, so none of the directions below may insert braces or refuse.

The shapes, and what each does today:

- **A macro used as a whole statement, with no `;`.** Stock does this: `WAIT_SERVER_FRAME` is
  `#define WAIT_SERVER_FRAME {wait(SERVER_FRAME);}` and is written bare. As an unbraced body the
  tracker never sees the body end: the
  NEXT statement is indented as if it were the body, and a function's closing `}` after one comes
  out indented. The blank line after the chain lands after the wrong statement too.
  ```gsc
  	if ( x )
  		WAIT_SERVER_FRAME
  		y();             // indented as the body; it is not
  	z();
  ```
- **A macro that expands to several statements**, such as `#define TWO_CALLS a(); b();`, used as an
  unbraced body. The source shows one statement under the `if`; after expansion only `a();` is
  conditional, and the blank line after the chain makes the wrong reading look deliberate.
  That one is a lint candidate more than a formatter one.
- **A macro that expands to a header**, such as `#define IF_DEV if ( level.dev )`. The tracker keys on
  the `if` token, so it never opens a body: no indent and no blank line. Expected from the design;
  not yet tried.

Possible directions: stand down (no pending indent, no owed blank line) when an unbraced body's
first token is a macro invocation, or ask the preprocessed stream whether the expansion ends in a
`;` or `}`. The first is local and safe; the second is exact but gives the formatter a
dependency on the preprocessor it does not have today.

### Should `fixCasing` reach variables and fields?

`gscode.format.fixCasing` covers keywords, functions, namespaces and class names, because each has
one authoritative spelling — the keyword table, a declaration or `#namespace` directive, or the
builtin library. Extending it to every identifier would make a file read consistently throughout,
but each remaining kind needs its own answer to "what is the right spelling", and some have none:

- **Locals and parameters.** Case-insensitive, scoped to one function. A parameter's declaration
  is an obvious source; a local has no declaration, only its first assignment, which is a weaker
  claim (the first write may be the typo). Needs the function's scope analysis, not a token pass.
- **Fields** (`self.health`, `level.wasp_enabled`). Case-insensitive, but they have no declaration
  at all: every file that writes one is equally authoritative. Engine fields do have a spelling in
  the object-field data (`origin`, `angles`), which would cover the common ones; script-defined
  fields would need a majority rule across the workspace, and the reference index does not keep
  spellings today.
- **Class methods.** `Base::take_damage()` recases `Base` but not `take_damage`, and `obj.Method()`
  is untouched: a method's spelling needs the receiver's class, which the lookup does not resolve.
- **Macros stay excluded whatever else is added.** They match 1:1: changing a macro reference's
  case changes whether it expands. Anything wider must keep the current guard.
- **Strings stay excluded.** Notify names, struct keys and asset names are compared by the engine
  as written.

Worth measuring first: how often the stock scripts and a real mod spell the same local or field two
ways. If that is rare, the churn of a wider fix may not pay for itself.

### `ArgumentCountLint` models builtin-versus-script resolution by spelling

The lint's comment reads the stock `earthquake` and `spawnSpectator` cases as "spelling decides
which one a call means", and the gsc-dialect-facts skill said the same. Both files support a
different model, and the formatter's casing now follows it: a bare call resolves to the builtin
first, whatever its case; a qualified call means the script function (`exploder_shared.gsc` reaches
its own `earthquake` only as `exploder::earthquake()`); and a threaded call cannot mean a builtin
(`_zm.gsc` threads its zero-argument `spawnSpectator()`, while the engine's takes two).

The lint's tie-break should be re-derived on that model and re-checked on the corpus. It currently
agrees with both files for a different reason, so a case the two models split on — a bare call
spelled like the script function rather than the builtin, say — is where it would be wrong.

---

## Known limitations from the triage pass

Recorded because each was a deliberate stopping point, not an oversight. Neither needs work unless
a real file shows the gap.

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

It serves the same shape we bundle, from `site/src/lib/apiSource/`. The payload carries its own
version marker, so "is there something newer" is answerable:

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
  changes that identity, wiping the SQLite cache and triggering a cold index (about 0.5 s on
  bo3's stock scripts today, 11.5 s at 50,000 files — PERF.md). Correct behaviour, but it should be a conscious trade rather than a surprise.
- **Validate before replacing.** A truncated or empty 200 must not wipe the builtin library:
  parse it, require a `revision` newer than the bundled one and a non-empty `api[]`, and only
  then swap.

**It buys nothing today** — the site now serves exactly what we bundle. It did not, and the way
that happened is the argument for building this properly rather than the argument against: both
sides carried T7 revision 32 with the same 2,191 entries, while the bundled copy had three GSC
entries hand-corrected (`BadPlace_Cylinder`, `DebugStar`, `Print3d`, each flagged `corrected`) and
two CSC entries the site lacked entirely (`DebugStar`, `Print3d`). Nothing detected it, because the
REVISION did not move — a curation pass edits entries without bumping the number the endpoint
reports.

So the version marker above answers "is there a newer revision", not "is this the same data", and
an update path built on `revision` alone would have carried the stale copy forward indefinitely.
Whatever gets built should compare content, and the duplication that allowed the drift — the same
library tracked in `server/src/GSCode.Workspace/Api/` and again in `site/src/lib/apiSource/` —
should become a copy step rather than two files someone has to remember to edit together.

### 2. Curate the dev-only builtin list

`Api/DevOnlyBuiltins.cs` drives the `DevOnlyFunctionCalledOutsideDevBlock` diagnostic for engine
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
`GSCode.Server`, so Workspace + Parser are a complete LSP-free engine — the composition is what
`TestWorkspace` and `HandlerWorkspace` already do in the test suites. Nobody has asked for it, so it
ships only if wanted. The decisions it would need: how a folder outside a game install finds its raw
root (the `gscode.rawPath`/`modsPath` settings have no CLI home yet), and which severities fail the
run.

### 4. The scale levers measured and parked

The 10K–50K pass (PERF.md, the scale section) brought every row inside its budget and left four
levers measured but unpulled, each with the condition that would change that:

- **Stat-first freshness on a warm start** — skip reading and hashing a file whose size and mtime
  match the cache. `index.read` is now 40% of warm thread-time, the largest share; the warm start is
  4 s at 50K against a 10 s budget. Worth doing when that row moves. A cache-schema change.
- **Streaming `LoadAll`** — 305 ms and 333 MB at 50K, transient. Only if memory at startup matters.
- **The post-index compaction pause** — 1.8 s at 50K, once, blocking. Requests arriving in that
  window wait for it.
- **bo3 find-references and rename** grow with the size of their ANSWER (406,326 locations in the
  sampled requests at 50K), not with a walk. A lazier location list is the only lever, and nothing
  has asked for it.

### 5. What the 2026-09-23 audit found and did not take

A read of the handler paths, the retained-memory shape and the cache path after the scale pass. What
it found worth doing has shipped (the git history for that date, and PERF.md for the two
measurements). These are the rest, with what each would cost, so the next reader starts from the
shape rather than re-deriving it.

**Per-request work nothing caches per document version.**

- **Semantic tokens** build the whole file's token list, a `claimed` set and a sort on every request,
  with no per-`ParseResult` cache — the one `InlayHintHandler` and `HoverHandler` both have. The win
  is real but smaller than theirs: a keystroke produces a NEW parse, so it would only hit on repeated
  requests at one version, which is what `range` requests during a scroll are. `Range = true` is
  advertised and the range request runs the identical whole-file `Tokenize`, which is the larger half.
- **Formatting** computes the WHOLE document's edits and the range and on-type handlers then filter
  them, and on-type fires on every `;` and `}`. The formatter also lexes its text four times
  (`AssignmentAligner`, `ColumnAligner`, `FormatScope`, and the corruption guard in `GscFormatter`)
  and line-splits it about six. The guard lex must stay; the aligners run in sequence on
  progressively rewritten text, so sharing one lex between them is a redesign of how they hand work
  along rather than a rename.
- **`Foo::bar()` where `Foo` is both a namespace and a class resolves differently in signature
  help and in inlay hints.** Signature help tries the class first, the hints try the namespace
  first, and each says so where it does it. `CallResolution` shares everything else the two ask
  about a call site; this one was left rather than settled by a refactor, because either order
  changes a shipped answer. Measured on BO3: 3 names collide (`phalanx`, `robotphalanx`,
  `throttle`) against 427 namespaces and 37 classes, so the decision is cheap to make and cheap to
  defer — but it is a decision, not an oversight.
- **The scale sweep does not time inlay hints.** `ScalePerfTests.Handlers` covers codeLens,
  references and rename; this handler runs per keystroke and has no row, so the flat-between-10K-
  and-50K property is argued from the code rather than measured. It holds by construction today:
  the merge-dialect route is scoped to the asking file and its direct includes, and the path route
  asks `FilesAt` for one path — neither touches `AllRecords`. Adding the row is what would keep
  it true.

**Reads still wider than their answer.**

- **Workspace symbol search** walks every record and every declaration in both stores, and an EMPTY
  query matches all of them — which is what VS Code sends when the symbol picker opens.
  `MaxResults = 256` is applied after the full collection AND after shadowing. **An early exit is not
  available without changing the answer**, and that is the finding rather than a detail:
  `ApplyShadowing` decides over the SET, so a raw match kept early can still be shadowed by an
  overlay found later, and a walk that stopped cannot know that. The real fix is the prefix or
  trigram index PERF.md parks — or its cheaper form, matching against `DeclarationIndex`'s distinct
  key set through `CollectKeys`, which completion already uses, and resolving to records afterwards.
- **`DatabaseQueries.FindReferences`** narrows the FILES by index and then scans each candidate
  file's entire reference list for the key. Affects references, rename, CodeLens and call hierarchy.
- **`WorkspaceDiagnosticsPublisher.Refresh()`** walks every record in the database, and
  `DependentDiagnosticsRefresher` calls it after re-linting a handful of closed dependents. Off the
  request path and debounced, so not a keystroke cost — but the caller already knows which paths
  changed.

**Memory, and the measurement that does not exist yet.**

- **The warm arm's retained memory has never been measured at scale.** Every figure in the scale
  table is a COLD index, and `RecordSerializer`'s reader builds strings straight from UTF-8 with a
  per-BLOB string table that never touches `NameTable` — so a warm start at 50,000 files may hold
  private copies of every name, namespace and path per record. PERF.md closed this concern in 2026-07
  on a 1,105-file corpus, where the warm live set came in 3.9 MB LOWER; that is not the same question
  at 50K, where every generated copy imports the same stock utilities. **Add the warm-arm row to
  `ScalePerfTests` first** — it is worth having whatever it says — and intern on restore only if it
  shows a gap, re-checking that the CPU cost does not move the 4 s warm start.
- **Single-entry collections allocated per file or per path segment.**
  `LanguageStore._overlayContextsByRelativePath` allocates an inner `ConcurrentDictionary` per
  distinct relative path with any non-raw record — roughly one per file in a mod-rooted tree, almost
  always holding one entry. `PathTreeIndex.Child.ByContext` is a `Dictionary` per path SEGMENT, the
  same shape. `PackedInvertedIndex` already solves this exactly: hold the bare value, promote to a
  set on the second. The indexes cost about 50 MB at bo3 50K against 2.3 GB, so size it first.
- **`FunctionSymbol.Doc` keeps `ScriptDocComment.RawText`** — the whole doc-block body of every
  documented function — for CLOSED files, and only hover reads it. Measure what it costs before
  deciding: dropping it changes what a closed file can answer.

**Analysed and NOT a lever, recorded so the idea is not re-derived from the shape:**

- **The preprocessor's `[.. _output]`** looks like a second full `PToken` array that a pre-sized
  builder should avoid. It is not avoidable: the pre-size is a ratio and the result must be exact, so
  a list backing array plus an exact array is the minimum either way — `ImmutableArray.Builder` with
  `MoveToImmutable`, or `Array.Resize`, allocate and copy identically. The only way out is giving
  `PreprocessResult` a buffer-plus-length instead of an `ImmutableArray`, which is a parser-wide API
  change for one allocation.
- **`Lexer`'s `ToImmutable()`** has the same shape and the same answer. Pre-sizing its builder by a
  characters-per-token ratio is separately REJECTED with evidence in PERF.md (bo1 486 to 596 MB of
  holes).

**Still worth measuring, in the cache path.**

- **Restore reads every file TWICE.** `WorkspaceIndexer` reads and hashes the file, and then
  `DiskStillMatches` reads the whole file AGAIN and compares the entire string, on both the restore
  and the analyse path. It is a race guard whose real backstop is the watched-file event, and it is
  not part of the stat-first trade above — it was never measured separately. A streaming byte compare
  would keep the guarantee exactly and drop a file-sized string per file; a stat compare would drop
  the read but weaken the guard.
- **Enumeration is serial and blocks every worker** (`WorkspaceIndexer`'s own doc: 295,640 files to
  find 1,105). PERF.md measures 0.1 s at 50K with a WARM OS cache, which is the best case and not a
  user's first start. Feeding `Parallel.ForEachAsync` from the enumerable through a channel is the
  change — but **measure a cold-OS-cache, game-install-rooted workspace first**, and do not make it
  if the gap is not there: it is the most structural item here and the only one that touches indexing
  order.

### 6. Dialect-to-dialect transpiler — groundwork removed, and how to bring it back

The idea: translate a script between GSC dialects, BO3 to the earlier games or back. **No transpiler
was ever written** — no rewriter, no plan, no entry here. What existed was groundwork in the typing
layer, built ahead of it on 2026-08-12 (`751c2711..a7d719fb`) and removed on 2026-09-28 (`20aeeded`,
`dd7a2081`) because nothing outside its own tests called it, while the docs justified the whole
typing layer by a transpiler that had no code. Restoring it is reasonable; restoring it with no
caller again is not — that is exactly why it went.

**What stayed, because other features read it:**

- `ScrValue` / `ScrTypeSet` / `ScrOperators` (Core/Symbols) — the union lattice with constant
  folding. Read by the typing lints, hover, inlay hints and go-to-type-definition.
- `FlowTyper.InferValues` → `ScriptTypes`, the value of every expression keyed by node, and
  `FlowTyper.TryGetValueAt`. This per-node map is the surface a rewriter walks; it was built for one
  (`7a2c9101`) and kept because the lints adopted it.
- `GameProfile.ArraysPassedByReference` (set on BO3 only). It has no reader today.
- `GAME_PROFILES.md`'s category matrix, which is in effect the list of what a translation has to
  change: import style (`#using` + `#namespace` vs `#include`), pointer style (`&foo` vs
  `maps\x::foo`), path calls, keywords (`foreach`, `class`, `function`, `do`, `const`, …), the
  preprocessor and `#insert` (BO3 only), ScriptDoc style, global objects, and the array fork below.

**What was removed, and where to get it back:**

| removed | was | restore from |
|---|---|---|
| `Workspace/Typing/ParameterTypes.cs` + `ParameterTypesTests` (13 tests) | what each parameter holds, unioned from the arguments every call site in the SAME FILE passes; omission folded in from both directions | `git checkout 20aeeded^ -- server/src/GSCode.Workspace/Typing/ParameterTypes.cs server/tests/GSCode.Workspace.Tests/Typing/ParameterTypesTests.cs` |
| `ScrValues.IsByReference(type, arraysByReference)` | whether a value aliases when passed, given the dialect | `git show 20aeeded -- server/src/GSCode.Core/Symbols/ScrValue.cs` |
| `ScrValues.IsAssignableTo`, `ScrTypeSet.AlwaysByReference`, `ScrValue.Nothing` / `IsExact` / `OfEntity` / `EntityKinds` | assignability with coercions; entity subtypes (never filled in production) | same diff |
| `ScriptTypes.ImprecisionHistogram` | how many expressions were unknown, by reason — the coverage number a rewriter would be budgeted against, per game | same commit, `ScriptTypes.cs` |
| `ScrImprecision` and `ScrValue.Imprecision`, `ScrValue.EngineBound` | WHY a value is unknown (untyped parameter, array element, script return — twelve reasons) | `git show dd7a2081` |

The ScrValue pieces cannot be restored by checking the old file out: `ScrValue.cs` has changed since
(the 2026-09-30 allocation work packed `ScrConstant`). Re-apply them from the diff by hand.
`ParameterTypes.cs` refers to `ScrImprecision.UntypedParameter`; without the reasons it returns
`ScrValue.Unknown` there instead.

**The hard part is one question: is this parameter an array?** Arrays are the only kind whose pass
semantics fork — BO3 passes them by reference, every earlier game copies them, while entities and
structs alias everywhere (`GAME_PROFILES.md`, the reference-semantics table). So a callee that
mutates an array parameter behaves differently after translation in either direction, and "is this
an array" has three answers — certainly, certainly not, cannot tell — where the third has to be
escalated to the user rather than guessed. The lattice already answers `MustBe(Array)` separately
from `MayBe(Array)`; what it cannot do alone is know what a parameter holds, which is what
`ParameterTypes` was for.

**Same-file inference is not enough for shared utilities.** A call's arguments live in the caller's
syntax tree and `ScriptRecord` keeps no tree, so cross-file means re-parsing the callers. The
original ParameterTypes comment priced that at ~44 ms a file; that was the corpus harness's
wall-clock, and analysis is ~0.4 ms a file across the indexing cores (PERF.md, 2026-09-15). So
re-parsing on demand is affordable at stock size and is not for a utility called from thousands of
files. The scaling answer is an argument index built during indexing and stored on the record — per
call site, the callee key and the typed value of each argument — which is a record-format change
(`RecordSerializer` + `CacheSchema.RecordFormatVersion`).

**A route back, if it is wanted.** A proposal, not a decision:

0. Write the plan and its entry here first, naming the caller that will use each piece. Nothing below
   lands ahead of the code that reads it.
1. Decide the direction and scope: which source and target games, whole files or a folder, and what
   the tool emits when it cannot decide (a comment, a diagnostic, a refusal).
2. Build it as its own LSP-free project over Workspace + Parser — the same composition the tests'
   `TestWorkspace` uses, and the one the headless CLI (item 3) would use. A transpile command could be
   a CLI verb.
3. Restore `ParameterTypes` and `IsByReference`, and give `ArraysPassedByReference` its first reader.
4. Restore `ScrImprecision` only if the rewriter's output depends on WHY a value is unknown. It took
   part in `ScrValue` equality, which is what the flow pass's fixpoint compares, so re-adding it needs
   the before/after corpus diff `dd7a2081` ran.
5. Measure coverage per game over the corpora (the histogram) before writing rewrite rules, so the
   unknowns worth attacking first are known rather than guessed.
6. Add the cross-file argument index when same-file coverage proves insufficient.
7. Verify on the corpora: every translated stock script parses under the target profile with no
   1xxx/3xxx errors and no new 5xxx Errors, and translating there and back reproduces the original
   token stream wherever no array question was escalated.

---

## Decided — not doing

### Formatter line wrapping

The formatter does not wrap long lines and will not. Everything else the formatter follow-up once
listed has shipped — `padParens`, `maxBlankLines`, per-request `tabSize`/`insertSpaces`, consecutive
alignment (`alignConsecutive`), directive sorting, and on-type formatting scoped to the alignment
group around the cursor. On-type formatting is opt-in: it runs only where the user enables
`editor.formatOnType` for the GSC languages, since a default that rewrites neighbouring lines on
every `;` proved unwelcome in 2.0.0.

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

**`gscode-5006 DevOnlyFunctionCalledOutsideDevBlock` — 6 Errors, all GENUINE.** Checked site by site
against the BO3 corpus; the standing suspicion that the callers were themselves dev-only is wrong,
and no change to `DevBlockCallLint` is warranted:

- `util::error` (×3, `_globallogic_audio.gsc:225` and `:496`, `_zm_weapons.csc:134`) — declared
  inside a `/#` at `scripts\zm\_util.gsc:15`, and every call site is an ordinary `else if` branch.
  There is no non-dev `error` in namespace `util` for GSC to fall back to; the one in
  `util_shared.csc` is client-side only.
- `debug_spherical_cone` (`_microwave_turret.gsc:467`) — dev-only in `util_shared`, called from
  outside a dev block in another file.
- `printHashIDs` (`_zm.gsc:419`) — declared inside a `/#` at `_zm.gsc:7136`. Worth knowing that a
  naive delimiter count says otherwise: the only `/#` before the call is on line 47, inside
  `//#using scripts\zm\_zm_hero_weapon;`, where the comment slashes abut the directive's hash. The
  lexer is right and the eyeball is wrong.
- `Print3d` (`vehicle_shared.gsc:3929`) — the interesting one. `show_node_debug_info` and
  `print_debug_info` are plainly MEANT to be dev-guarded: there is a closing `#/` on line 3932. But
  no `/#` opens it — the nearest one, at 3287, is closed at 3292 — so the guard never begins and the
  functions really do call a dev-only builtin outside a dev block. A stray delimiter in the stock
  scripts, surfaced by the lint doing its job.

**`UnusedUsing` (2,187 at last sweep)** — also real: a text scan for the imported file's namespace,
with comments stripped, finds an actual `ns::` use in **zero** of them. Stock scripts simply carry a
lot of stale imports. It is a Hint, so they grey out rather than nag.

### Corpus grammar gaps (1 of the 3 found still open)

The corpus run over real `share\raw` found 4 failing files out of 980, from three causes. Two were
fixed — `&"..."` parsing as address-of instead of an istring, because it also broke the spaced form
`& "loc"` in ordinary hand-written code; and a dev-block close with nothing open. The remaining one
is **deliberately left**: the game has shipped, so these stock files are frozen, and the pattern
does not justify a grammar change. Diagnosis kept only because it was already done:

- **`gib.gsc(58)` / `gib.csc(35)`** — `#define GET_GIB_BUNDLES struct::get_script_bundles(...)`
  is object-like, but the call site writes `GET_GIB_BUNDLES()`, so expansion yields a call
  applied to a call result. Would be fixed by letting `ParsePostfixChain` accept `(` alongside
  `[` and `.`.

The corpus test prints it on every local run, so it cannot be quietly forgotten.
