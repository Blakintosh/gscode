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

### Variadic builtins are not modelled, so a builtin call has no upper argument bound

`ArgumentCountLint` treats a builtin's mandatory count as a LOWER bound and stops there. The upper
bound was written, measured and withdrawn: it reported 634 Errors across 134 shipped BO3 scripts,
and every one was the library under-declaring rather than the script over-calling — `Array( a, b, c )`
is variadic against a single declared parameter, `Record3DText` takes six against one. The full
reasoning sits on `InspectBuiltin`; this is the entry `ARCHITECTURE.md` points at for it.

**The data has since grown the marker the check needs.** A parameter's type is structured in the
bundled JSON (`"type": { "dataType": …, "isArray": … }`) and `vararg` is one of its spellings — 34 of
BO3's 2,191 GSC entries carry it and 15 of its 803 CSC ones, both names above among them.

**Step 1 below is now done** (corrected 2026-08-12; this entry said the marker was still being lost
in `FormatType`, and it is not). `ApiLoader` passes `IsVararg(parameter.Type)` into
`BuiltinParameter.IsVariadic`, and alongside it `ParseType` puts the declared type on the lattice as
`BuiltinParameter.Types`. Neither has a production reader: `IsVariadic` is read only by
`ApiTypeParsingTests`, `Types` by nothing at all, and `ArgumentCountLint` still consults only
`Mandatory`. So the remaining work is steps 2 and 3 — the per-game measurement — not the carrying.

**Carrying the marker is the easy half; coverage is the whole problem.** The bound is only worth
having where `HasReliableBuiltinSignatures` holds, which is CoD4 and BO3 — and CoD4's 819 entries
mark no vararg at all, while BO1 marks 10, WaW 2 and MW2 none. So on one of the two eligible games
the marker is certainly incomplete and on the other it is merely untested, and an upper bound shipped
on a marker that is mostly-there is the 634 again.

Route, in order:

1. Carry `vararg` as a flag on `BuiltinParameter` rather than losing it in `FormatType`. Additive —
   nothing reads the current string for anything but display.
2. Re-run the upper-bound check per game with vararg parameters exempt, and read the TOP REPORTED
   NAMES rather than the count: a shape shared across them is another library gap, not a user
   mistake. `tests/GSCode.Server.Tests/harvest/*_client_arity.json` is already this measurement in
   miniature — it records `declaredMax` against observed counts (`PlaySound`: 1 declared, 2–4
   observed across 190 calls).
3. Ship it per game only where the remainder is zero, the way `HasReliableBuiltinSignatures` was
   earned in the first place.

Worth knowing before step 3: the JSON also carries per-entry `flags` and `confidence` that the loader
drops entirely (BO3's GSC library: 157 `verified` against 2,032 `processed`; BO1's and MW2's carry 259
and 264 `aiGenerated`). If a game's remainder will not reach zero, that is the discriminator for a
weaker severity rather than none — it is what 1.5 used to split `ArgumentTypeMismatch` from its
`Unverified` twin.

### 1.5's type-derived diagnostics have no counterpart here

This tree is at parity with 1.5 or ahead of it in every diagnostic layer but one — lexing, preprocessing,
parsing, extraction, resolution and arity, across five dialects rather than one — and the exception is a
coherent family rather than a scatter. 1.5 ran an abstract-interpretation pass — `CFA/ControlFlowAnalyser`
built a CFG and `DFA/TypeFlowAnalyser` (3,166 lines across three partials; the `DFA/` and `CFA/` folders
together ~290 KB with `ScrData`, `ScrEntity` and `OperatorSemantics`) walked it over a 17-BIT FLAG
lattice with real unions (`Int = 1 << 1 | Bool`, `Number = Int | Float`) and entity subtypes.
Seventeen codes came out of it that nothing here raises, plus the type half of an eighteenth:

**Correction — this entry used to credit 1.5 with "constant-value tracking" as well. It had none.**
`ScrData` carried exactly one value-level fact, `bool? BooleanValue`; every arithmetic operator
returned a fresh valueless type, and its divide-by-zero check tested `right.BooleanValue == false` —
falsiness standing in for zero, which misses `2 - 2` and fires on `x / ""`. Constant folding was a
from-scratch build here, not a port.

| family | 1.5 codes |
|---|---|
| operators and conversions | `OperatorNotSupportedOnTypes`, `NoImplicitConversionExists`, `DivisionByZero` |
| argument types vs. the builtin library | `ArgumentTypeMismatch`, `ArgumentTypeMismatchUnverified` |
| `const` | `CannotAssignToConstant`, `ExpectedConstantExpression` |
| member, index, enumeration, vector component | `DoesNotContainMember`, `CannotUseAsIndexer`, `CannotEnumerateType`, `InvalidVectorComponent` |
| engine field data | `PredefinedFieldTypeMismatch`, `CannotAssignToImmutableEntity` |
| function values | `StoreFunctionAsPointer`, `ExpectedFunction` |
| threaded calls | `ConsumedThreadedCallResult` |
| statements | `InvalidExpressionStatement`, and the type-compatibility half of `UnreachableCase` |

Three more went in the same removal and are NOT type-derived, which makes them separable and much
cheaper: `MultipleDefaultLabels` (duplicate `default:`, raised from the CFG builder — it belongs beside
`CaseLabelLint`'s 5017 and needs no types at all), `AssignOnThreadedFunction` (an assignment whose
right-hand side contains a `thread` call — a plain AST walk in `SPA/ScriptDiagnosticsAnalyser`, no
lattice involved), and `DuplicateMacroDefinition`/`DuplicateMacroParameter` from 1.5's preprocessor,
which are a 2xxx-band rule about directives.

**Correction — an earlier version of this entry said 1.5's analysis "was largely switched off", and
that is wrong.** The commented-out files it cited are real: `SPA/Logic/Analysers/Analysers.cs` is 398
of 399 non-blank lines commented, `AST/Expressions/OperatorData.cs` 913 of 926 (12 live lines, no type
declared), `ExpressionAnalyzer.cs` 143 of 157. But those were a SUPERSEDED generation, not the live
one, and the inference drawn from them was backwards. Checked against the tag:

- `v1.5.0:server/GSCode.Parser/Script/Script.cs:315-321` constructs and runs `ControlFlowAnalyser`
  then `DataFlowAnalyser` unconditionally, on every analysis, wrapped in nothing but `try`/`catch`.
- The `Silent` flag that looks like a kill switch (`DFA/AnalysisFlags.cs:5`, defaulting `true`) is the
  two-pass shape of a worklist fixpoint: `TypeFlowAnalyser.AnalyseFunction` sets it `true` while
  iterating (`:104`) and flips it `false` at `:351` for a final emitting pass over every visited node.
- The live operator implementation is `DFA/OperatorSemantics.cs`, raising `OperatorNotSupportedOnTypes`
  at 20 sites and `DivisionByZero` at 2.
- `GSCode.NET/LSP/Handlers/CodeActionHandler.cs` shipped quick fixes keyed off five of these codes
  (`:70`, `:78`, `:86`, `:106`, `:134`), and `GSCode.Tests/ScrDataApiTypeTests.cs:149,181` is a
  false-positive regression test for `OperatorNotSupportedOnTypes`.

Zero of the twenty-one were switched off. Only `AssignOnThreadedFunction` was gated at all, and only
to editor mode (`Script.cs:325`). What remains true is the narrower point: the parts still live carried
their own noise admission, since the `ArgumentTypeMismatch`/`Unverified` split exists precisely because
the library's declared types could not be trusted enough for one severity. `client/CHANGELOG.md`
carried the same wrong claim and has been corrected with it.

**What this tree already has to build on:**

**Correction — this bullet described the state before the lattice landed, and was left standing after
it did.** It said `FlowTyper` was 910 lines over a flat `ScrType`, and that against 1.5 the tree was
missing unions, constant values, entity subtypes and an environment retained per position. All four
of those exist now, and the entry below on step 1 contradicted this one for weeks rather than editing
it. That is the failure mode this file keeps producing: a reader arriving here first gets the old
picture and no signal to keep reading. What follows is the current state.

- `Typing/FlowTyper.cs` (1,285 lines) — a forward per-function walk that types assignments from
  literals, arithmetic, globals and builtin return types. It carries `ScrValue` internally and
  projects to `ScrType` at its public boundary, so hover, inlay hints, `PreferBooleanLiteralLint`
  (5002) and `ReadOnlyWriteLint` (5004/5005) still read the flat 12-value enum and were untouched by
  the change. What produces nothing is still UNCERTAINTY, but the reason moved down a layer:
  `ScrType.Join` collapses any disagreement to `Unknown` because it is a PROJECTION of a union, not
  because no union was computed.
- The four gaps against 1.5 are closed. Unions are `ScrTypeSet`'s disjoint bits with `ScrValue.Union`;
  constant values are `ScrConstant`; entity subtypes are `ScrValue.EntityKinds`, unioned at joins;
  and the per-position environment is `InferValues` returning a `ScriptTypes` node map, with
  `FlowTyper.TryGetValueAt(result, position, out ScrValue)` for a single query. `ScrValue` goes
  further than 1.5 did in one respect it did not ask for — every imprecision carries a REASON.
- The API data is on the lattice too. `ApiLoader.ParseType` maps each parameter's and return's
  declared type onto `ScrTypeSet` once at load, including the pipe-separated unions (`"int | string"`)
  and `number`, and `ApiLoader.ParseConfidence` keeps the per-entry `high`/`medium`/`low`.
  `FlowTyper` reads the return types and the confidence; **nothing reads the parameter types**, which
  is `ArgumentTypeMismatch`'s row in the table below. `VoidResultLint` (5019) remains the standing
  proof that a rule can be driven off this data without a lattice at all.

**The route back, and the order it has to go in:**

1. **Compute and emit have to separate first.** The two lints that use FlowTyper today read
   `InferAssignments` — a list of assignment SITES — and emit from their own walks. A type rule needs
   the environment AT a position instead, and the walk does not retain one; that is the same missing
   piece as the hover-join limitation below. Built once it serves hover, the join and every rule after;
   built per rule it serves none of them.
2. **One rule at a time, each measured over the corpus before it is given a severity.** `add-diagnostic`
   step 6 is not optional here, and this family is exactly the shape that fails it: `GSCode.Workspace/FOLDER.md`
   states the rule as an Error must never land on code that ships and works, and GSC's typelessness means
   most of these can honestly only ever be Warnings. The evidence that the data cannot carry an Error
   already exists — the mandatory-COUNT check alone reported 141, 280 and 157 findings on CoD4, WaW and
   BO1 from library errors, and the builtin upper bound 634 on BO3. A type check depends on the same data
   more heavily than a count does.
3. **Cheapest first, because each is worth having alone.** Duplicate `default:` needs no types.
   `DivisionByZero` on a literal zero divisor needs constant folding of a literal, not a lattice. `const`
   validation needs neither: `ConstDeclNode` is already in the AST and both `UnassignedVariableLint` and
   `UnusedLocalLint` already walk it, so "assigned after declaration" is a syntactic question.

Restoring the family AS a family is the one approach to rule out. It is what 1.5 did, and commenting the
result out is what 1.5 then had to do about it.

### Step 1 is done — the lattice exists, and nothing raises a diagnostic off it

Built as infrastructure for a future dialect-to-dialect transpiler rather than for a rule, which is
why it went in despite the family above staying shut. `ScrValue` (Core/Symbols) is a union lattice
with disjoint bits, constant values, tri-state truthiness, entity kinds and — the piece no linter
would have asked for — a REASON attached to every imprecision. `ScrOperators` is the operator table.
`FlowTyper` carries it and projects to `ScrType` at its public boundary, so hover, inlay hints and
the two typing lints are untouched and every one of the 42 typing tests passed unedited.

What that bought, none of it surfaced to a user:

- The `NumericResult` bug above is fixed. `vector * 0.5` types as a vector.
- Builtins can produce an array. `isArray` was dropped by the loader, so `ScrType.Array` was never
  once produced by a call — the engine was confident about structs and entities, which are the SAFE
  kinds, and silent on arrays, the only unsafe one.
- `number` (349 declarations on BO3's GSC library) and pipe-separated unions (`"int | string"`) now
  parse, and `confidence` survives loading — which is where `ArgumentTypeMismatchUnverified` would
  get its severity split from, if that pair is ever restored.
- `InferValues` gives a per-node map and `ImprecisionHistogram` a coverage count by reason, so "how
  much of a file can be translated" is measurable rather than guessed.

Two things it did NOT change, deliberately: no new diagnostic, and no movement in the corpus. Every
sweep across the five games reported identical counts before and after.

### What has since been restored, and what the attempt taught

Eight of the twenty-one are back, all from the tier that needs no type information at all — the third
step above, taken in order. `2017`/`2018` (the duplicate-macro pair), `5027` (a second `default:`),
`5028` (reading the value of a threaded call, merging 1.5's `ConsumedThreadedCallResult` and
`AssignOnThreadedFunction`, which were one mistake counted twice), `5029`/`5030` (the `const` pair),
`5031` (a literal-zero divisor) and `5032` (a statement with no effect). Each was swept over the five
corpora before being given a severity; all report zero on shipped code except `5028`, whose 172 are
genuine instances of the pattern.

Two of them only became sound because the sweep contradicted the obvious implementation, which is
worth keeping:

- `5030` collecting `const` names FILE-wide reported ten writes on BO3, every one an ordinary local in
  a different function that shared the name (`_hud_message.gsc`'s `duration`,
  `vehicle_death_shared.gsc`'s `max_angluar_vel`). The scope is per function.
- `5032` reported nine statements across the five games and not one was a statement with no effect —
  every one was recovery wreckage after a parse error, including the known `gib.gsc(58)` gap and
  bo1's `= % o_full_interstitial_01_camera;`. It now stands down on a file the parser could not read.

**`PredefinedFieldTypeMismatch` was written, measured and withdrawn**, which is the useful part. It
needs no lattice — `FlowTyper` knows the assigned value's type and the object-field data states the
field's — and it was built by exclusion (only combinations that cannot be right), scalar declared types
only, `undefined` always allowed. It still reported **46 findings on BO3 and zero elsewhere, none of
them real**, from two separate causes:

1. **The object-field data is wrong for several fields.** `self.team = self.sessionteam;`
   (`_globallogic_player.gsc:968`) reports because the data types `team` as `int` and `sessionteam` as
   `string` — the two contradict each other and both hold team strings. `horzalign`/`vertalign` are
   typed `int` and are assigned `"user_right"` throughout `hud_util_shared.gsc`; `combatmode` and
   `type` are the same shape. Fixing the data is the prerequisite, and this list is the worklist.
2. **A real bug in our own inference.** `NumericResult` (`FlowTyper.cs:847`) returns `Float` whenever
   either side is `Float`, so `vector * 0.5` types as Float and `self.velocity = self.origin * 0.5`
   reports. This is wrong for hover and inlay hints TODAY, independently of any lint — 1.5's
   `OperatorSemantics` had it right, casting upward to vector when one side is numeric. Worth fixing
   on its own; it needs the operator passed in, since `vector * vector` is not `vector + vector`.

So the tier-3 warning above is now measured rather than predicted: a type rule fails on this data
before it fails on the lattice.

**`CannotEnumerateType` (5033) and `InvalidVectorComponent` (5034) are now restored**, which is what the
union lattice bought. Both report zero across all five corpora and both carry controls that must fire,
since a rule that is silent everywhere is indistinguishable from one that does not work.

**`OperatorNotSupportedOnTypes` was written alongside them, measured and withdrawn** at 752 findings
on code that ships and works — the same ending as `PredefinedFieldTypeMismatch`, and worth the same
detail because the two causes are different traps:

1. The guard tested `ScrValue.IsUnknown`, which is exact equality with the universe. A value narrowed
   by `isdefined` is the universe MINUS undefined, so it is no longer "unknown" by that test while
   still knowing nothing. Any future rule guarding on `IsUnknown` has this hole.
2. `vector + scalar` reports as unsupported and appears throughout the stock scripts. The operator
   table is stricter than the engine, so the table itself is not yet a sound basis for a diagnostic
   even though it is a fine basis for TYPING. Fixing the rule cannot fix that; the table has to be
   corrected against the corpus first, and nothing establishes what the engine actually does here.

**Still not restored, and what each needs:**

| Code | Blocker |
|---|---|
| `CannotUseAsIndexer` | `FlowTyper.TypeOf` returns a value for an `IndexNode` without typing the index EXPRESSION, so there is nothing to judge. Additive, but its own change. |
| `ExpectedFunction` | Needs `[[ x ]]()` to resolve what `x` holds. The lattice can say Function; what is missing is that nothing types a pointer dereference's operand. |
| `StoreFunctionAsPointer` | Not a type question: it needs to know a bare identifier names a function, which is resolution. Its complication is that `UnassignedVariableLint` already reports that identifier as `5016`, so it must REPLACE that diagnostic rather than stack a second one on the same range. |
| `PredefinedFieldTypeMismatch` | The two causes above. |
| `CannotAssignToImmutableEntity` | Not expressible in the data. `ObjectField` carries a per-FIELD `ReadOnly` flag and an `EntityKind`, and nothing marks a kind immutable as a whole. |
| `ArgumentTypeMismatch` / `…Unverified` | Not the plumbing — a corpus sweep. See below; this pair had no row here at all until 2026-08-12. |

**`ArgumentTypeMismatch` is the one of the twenty-one that went unaccounted for**, listed in the
family table at the top and named in neither this table nor the ruled-out list. That was an
oversight rather than a decision, and it matters because the pair is the piece the lattice most
directly enables — checking a call's arguments against the library's declared parameter types.

Its plumbing is already finished, which is the surprising part:

- `BuiltinParameter.Types` is the declared type **already parsed onto `ScrTypeSet` at load**, once
  per entry rather than re-switched per call. Nothing in the tree reads it — not one production
  caller, not one test. It exists solely for this rule.
- `BuiltinFunction.Confidence` is loaded and carries `high`/`medium`/`low` (1,291 / 684 / 80 on
  BO3's GSC library). That is precisely where the `Unverified` twin's severity split comes from, and
  the reason 1.5 needed a whole second CODE is that it had nowhere to put the distinction. We do.
- `FlowTyper` already reads `Confidence` for `ScrImprecision.BuiltinUnverified` and already reads
  `ReturnTypes`, so both halves of the pattern are in service elsewhere.

So the blocker is step 2 of `add-diagnostic`, not step 1: **measure it over the five corpora before
it is given a severity, and read the top reported NAMES rather than the count.** Treat a high count
as a library defect until proven otherwise. Every precedent points that way — the mandatory-COUNT
check alone reported 141, 280 and 157 findings on CoD4, WaW and BO1 purely from library errors, the
builtin upper bound 634 on BO3, `PredefinedFieldTypeMismatch` 46 and `OperatorNotSupportedOnTypes`
752, and in all five cases the inference was right and the data was wrong. A type check leans on
that data harder than a count does, so this rule is the most likely of the family to end the same
way. Ship it per game only where the remainder is zero, the way `HasReliableBuiltinSignatures` was
earned.

**Ruled out permanently**, with reasons, so they are not revisited as oversights:

- `DoesNotContainMember` — unsound in GSC. Fields can be added to any entity or struct at runtime, so
  "does not contain" is never knowable. 1.5 shipped it as an Error and needed a false-positive
  regression test (`StringSizeAndBreakTests.cs:73`) to hold it back.
- `NoImplicitConversionExists` — GSC truthiness accepts nearly everything, so the broad form has no
  sound core to narrow down to. Unions did not change that: the problem was never the lattice.
- `OperatorNotSupportedOnTypes` — written against the union lattice, measured at 752 findings on
  shipped code, withdrawn. See the entry above for the two causes; the second of them says the
  operator table is stricter than the engine, which is a data problem rather than a rule problem.
- The type half of `UnreachableCase` — needs the switch subject and every label typed exactly, and a
  label is usually a macro or a bare literal while the subject is usually a parameter. The
  duplicate-label half already ships as `5017`, and the duplicate-`default:` half now as `5027`.

---

## Known limitations from the triage pass

Recorded because each was a deliberate stopping point, not an oversight. P0, P1 and the
hover/doc half of P2 are done; the remaining items below are the decisions still worth making.

### Parameter inference stops at the file boundary

`Typing/ParameterTypes.Infer` reads the arguments passed at every call site IN ONE FILE and unions
them per position, which answers "is this parameter an array" — the question a dialect transpiler
blocks on, since whether an array parameter is mutated by its callee is the only behavioural
difference between BO3 and the earlier games. Two passes: the first types every expression with
parameters unknown, which is enough to type the arguments; the second seeds the parameters from
them. Not iterated to a fixpoint, so a parameter passed straight through to another call stays
unknown and says so.

**Cross-file is the part left, and the obstacle is structural rather than effort.** A call site's
ARGUMENTS live in the caller's syntax tree, and `ScriptRecord` stores extraction output — symbols,
references, dependencies — not trees. Reading arguments from another file means re-parsing it, and
the measurement already on record is ~44 ms per file, so roughly 43 seconds for BO3's 980 scripts on
every query. What would make it affordable is an argument index built during indexing and persisted
with the rest of the record: per call site, the callee key and the typed value of each argument.
That is a cache-schema change, which is why it is not folded into this.

Worth knowing before starting: the same-file half already covers a helper declared and called in one
script, which is most of what a per-file rewrite reasons about. The cross-file half matters for
shared utilities, where it matters most.

The field half of the old entry here is done: `HoverHandler.InferredFieldType` reports an inferred
type for a field the scripts invented, and `ScrValue` carries the reason when it cannot.

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
  changes that identity, wiping the SQLite cache and triggering a cold index (~5.5 s on 1,105
  files). Correct behaviour, but it should be a conscious trade rather than a surprise.
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

So the version marker below answers "is there a newer revision", not "is this the same data", and
an update path built on `revision` alone would have carried the stale copy forward indefinitely.
Whatever gets built should compare content, and the duplication that allowed the drift — the same
library tracked in `server/src/GSCode.Workspace/Api/` and again in `site/src/lib/apiSource/` —
should become a copy step rather than two files someone has to remember to edit together.

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

### 4. The site's library browser is Black Ops III's alone

The extension bundles eight builtin libraries across five games; `gscode.net/library` browses one.
Two places hardcode it — `site/src/routes/api/getLibrary/+server.ts` dispatches on
`gameId === "t7"`, and `site/src/routes/(gscode)/library/[languageId]/+layout.ts` sets
`const gameId = "t7"` — and only the T7 pair is imported under `site/src/lib/apiSource/`.

The seam is already there and is not the hard part: `library.ts`'s
`getLibrary(fetch, gameId, languageId)` takes the game, and `ApiLibrarian.initialise` is passed
one. What has to be decided is the URL shape, and it is a one-way door: a game route segment
(`/library/[gameId]/[languageId]`) is linkable and cacheable per game but moves every existing
`/library/gsc` URL and needs redirects, while a selector over the current routes keeps those URLs
and cannot put the game in a link. Sizes, since they land in the site bundle: CoD4 527 KB, WaW
772 KB (GSC + CSC), MW2 882 KB, BO1 1.23 MB (GSC + CSC) — about 3.3 MB on top of T7's 3.7 MB.

Not a release blocker; the extension has never read this endpoint. It is a claim-versus-reality
gap on the public site of a five-game release, which is why it is written down rather than left
to be noticed.

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
