# Post-rewrite follow-ups (P13 / P14)

P0–P12 are complete and green. This file tracks what deliberately did NOT ship in the
rewrite, split into the two remaining buckets. P14 was opened after a global audit of the
implementation against the original plan (2026-07-20) and holds every plan-promised item
found unimplemented.

Legend: **Missing** = no implementation at all · **Partial** = wired but incomplete ·
**Dead** = code/data exists but nothing consumes it.

---

## Known bugs (reported in use, 2026-07-20) — ✔ BOTH FIXED

Both were macro-navigation defects in shipped P7 behaviour, not plan gaps, and both were
fixed ahead of the remaining P14 waves because they sit on daily-use paths (`IS_TRUE`,
`REGISTER_SYSTEM`, `NEW_STATE` are everywhere in BO3 script). They were two distinct bugs
behind one symptom: B1 is a store-routing miss, B2 a range-attribution miss.

### B1 — Go-to-definition fails for a macro defined in another file — ✔ FIXED

`IS_TRUE` in `shared.gsh`, used from `array_shared.gsc`: the call site resolves as a `Macro`
reference, but the definition lookup returns nothing.

**Cause (traced):** the macro's `Definition` reference IS emitted — when `shared.gsh` is
analysed standalone its macros have `SourceFile == null`, so `SymbolExtractor` records one.
But `.gsh` records route to `ScriptDatabase._gshRecords`, while `DefinitionHandler` resolves
via `DatabaseQueries.FindReferences(target.Store, …)` with `target.Store` being `Gsc`/`Csc`.
**The GSH store is never consulted.** Find-all-references on a macro is broken identically,
same cause. Hover was never broken: `HoverHandler.FindMacro` reads
`target.Result.Preprocessed.Macros.All`, a different path that includes inserted macros.

The plan already specified this seam and it was never implemented — see the language-guard
invariant: *"standalone GSH queries — a `.gsh` serves both languages, so macro
references/rename from a GSH union BOTH stores"*.

**Fixed:** `DatabaseQueries.FindGshReferences` scans the shared GSH store; definition and
find-all-references union it in for `SymbolKind.Macro` hits. Covered by
`Database/GshMacroLookupTests`. Hover was confirmed unaffected — it reads
`Preprocessed.Macros.All`, whose inserted-macro resolution is already covered by
`InsertTests.Insert_MacroFromGsh_DefinitionSitePointsIntoGsh`.

### B2 — Macro-expanded content is attributed to the invocation site — ✔ FIXED

A multi-statement macro used in the same file, e.g. `NEW_STATE( "play" );` where the macro
body opens with three `flagsys::clear( … )` calls:

- Go-to-definition on `NEW_STATE` jumps to `flagsys_shared::clear()` — the macro body's first
  call — instead of the `#define`.
- Inlay hints render garbage at the call site: `str_flag: str_flag: str_flag: NEW_STATE:
  string( : "play" )`, one `str_flag:` per expanded `clear` call.

**Cause (traced):** `PToken.RootRange` is `Provenance.RootSite ?? Range`, and macro-expanded
tokens carry `RootSite` = the invocation range. `SymbolExtractor` records every reference at
`RootRange` unconditionally, so all ~8 references from the expansion stack onto the one
invocation range. Whatever sits first in the list wins go-to-definition, and every expanded
call contributes its own parameter hints at that position.

**Why the same-file case is the visible one:** the existing guards test
`Provenance.SourceFile is null`, but a macro defined in the same file HAS a null `SourceFile`,
so those guards don't discriminate. The correct test for "came from an expansion" is
`Provenance.DefinitionSite`/`RootSite`, not `SourceFile`.

**Fixed:** `SymbolExtractor.AddReference` drops any reference whose token carries a
`DefinitionSite`; the inlay-hint handler skips calls from an expansion, and FlowTyper skips
expanded assignments. Covered by `Extraction/MacroExpansionReferenceTests`.

Accepted cost, recorded deliberately: a function named ONLY inside a macro body now has no
reference anywhere, because the body is not parsed as code at its definition site either.
Find-all-references on such a function will not list macro-body uses.

Still open, same family: `#insert` frames also carry a `RootSite` (the directive's range), so
content spliced from a `.gsh` that declares real code — not just macros — would collapse onto
the `#insert` line in the including file's record. Not observed in practice, since headers
are macro-only by convention, and not addressed here because suppressing it could drop
function definitions that only reach a language store via an insert. Worth a decision if
code-bearing headers ever appear.

---

## Execution order (agreed 2026-07-20)

P13 and P14 are interleaved by leverage and user impact, not by phase number. Waves are
ordered; items inside a wave are ordered. Each item is one commit unless noted.

**Wave 1 — diagnostics foundation. ✔ DONE.** Highest leverage: one record change unblocks
three features, and the grey-out is the most visible missing behavior in the editor.
1. ✔ Extend `Core.Diagnostics.Diagnostic` with `Tags` + `RelatedInformation`, map both in
   `LspMapping` (P14 #1 infrastructure).
2. ✔ Inactive `#if`/`#elif`/`#else` grey-out via `DiagnosticTag.Unnecessary` (P14 #1).
   Needed no preprocessor change — `RecordDisabledRegion` already trimmed ranges and
   already excluded insert-provided regions; only the emission was missing.
3. ✔ Unused `#using` tagged `Unnecessary` (P14 #1) — `Analysis/UnusedUsingLint.cs`.
4. ✔ Duplicate-definition `relatedInformation` (P14 #1) — new `DuplicateFunction` (4005)
   rule in `SymbolExtractor`, file-local declarations only.

**Wave 2 — no dead surfaces. ✔ DONE.** User-mandated; both were shipped-but-inert.
5. ✔ `gscode.rawFileWarningMode`: `Api/StockScripts.cs` + `Resolution/RawWriteGuard.cs`,
   the `gscode/rawFolderWriteWarning` notification on didSave, and a client handler that
   warns once per file per session with a "Don't Warn Again" action (P14 #10).
6. ✔ Radiant map-entity keys wired to hover + completion (P14 #6). Also fixed a second
   dead-data case found while wiring it: engine object fields from `t7_object_fields.json`
   were never offered in completion either, despite a comment claiming they were.
   Measured contribution: 330 of 350 radiant keys are names the object-field data does not
   cover, 245 of them carrying documentation.

   Note for later: `classname` was the **only** key `keys.txt` marked client-only, and that
   marking is wrong — GSC reads it constantly. The field-data generator now corrects it to
   `both` (`CorrectSide` in `tools/field-data/Program.cs`), so the bundled artifact contains
   **zero** client-only keys and the `client`/`both` side filter is currently dormant. The
   mechanism is kept because the `client` prefix is still valid `keys.txt` syntax a tools
   update could reintroduce: it is unit-tested against synthetic data, and
   `BundledKeys_CarryNoClientOnlyEntries` fails loudly if one reappears so the correction
   table gets revisited. Engine object fields carry no side data at all — if per-language
   field accuracy ever matters, that is where the real gap is.

**Wave 3 — diagnostics carries. ✔ DONE.** All user-confirmed carries from the plan.
7. ✔ prefer-boolean-literal lint (P14 #2) — `Analysis/PreferBooleanLiteralLint.cs`. Scope
   recovered from the v1 regression test in git history: declared-`bool` parameters only.
   Lives in the workspace layer, not extraction, because it needs the builtin API.
8. ✔ Cross-namespace `private` function diagnostic (P14 #3) — `Analysis/PrivateAccessLint.cs`.
   **Semantics corrected 2026-07-20**: `private` in GSC is scoped to the NAMESPACE, not the
   file. A namespace can be split across files, and any file declaring it may call into its
   private members. Resolution, completion and the lint all follow the namespace rule now;
   `DatabaseQueries.DeclaredNamespaces` supplies the asking file's namespaces from the live
   parse result so unsaved edits count immediately.
   Also fixed a latent bug this exposed: `ScriptRecord.Path` was assigned the raw analysed
   path despite being documented as the normalized store key, so private visibility (a
   record-path vs asking-path comparison) could be defeated by casing or slash style.
9. ✔ ReadOnly-field write + `.size` write diagnostics (P14 #4, #5) —
   `Analysis/ReadOnlyWriteLint.cs`. `.size` is an Error (language-spec fact); engine fields
   are a Warning (curated data can carry mistakes, as `classname` proved). Only flags a field
   when every declaring entity kind agrees it is read-only — exactly 2 names in the current
   data (`type`, `radius`) are mixed and stay silent.

**Wave 4 — FlowTyper carries. ✔ DONE.** Ordered so the lattice landed before its consumers.
10. ✔ Branch-join convergence — `ScrType.Join` wired into the walk (P14 #9). Branches now
    walk cloned environments that are merged afterwards; a name typed on only one path
    becomes Unknown. Unblocked the `CfaTests` / `TypeFlowConvergenceTests` porting rows.
11. ✔ `isdefined` narrowing (P14 #8). Note its value here is the NEGATIVE arm: with a lattice
    this coarse, narrowing the positive arm changes no outcome, but narrowing
    `!isdefined( x )` stops a stale type being asserted on a path where the value is known
    not to exist. Only bare locals narrow; fields and indexes aren't tracked in the
    environment.
12. ✔ `BuiltinEmulations` table (P14 #7) — `Typing/BuiltinEmulations.cs`. Deliberately two
    entries. The table exists to cover callable KEYWORDS, which carry no builtin-API entry;
    of those, only `isdefined` (Bool) and `vectorscale` (Vector) yield a value worth typing.
    `profilestart`/`profilestop`/`waittill`/`waittillmatch`/`notify`/`endon` are
    statement-shaped, and in this lattice a void result is indistinguishable from Unknown,
    so listing them would change no outcome. v1 also emitted arity diagnostics from its
    emulation table; not carried, because checking arity for two keywords while every other
    function goes unchecked would be arbitrary — arity checking belongs to a general rule
    driven by the API's parameter lists.

**Wave 5 — workspace lifecycle.**
13. ✔ Untitled documents (P14 #13) — **verified, no code needed.** Probed the real behaviour:
    `GetFileSystemPath()` on `untitled:Untitled-1` returns the bare buffer name, and
    `Path.GetFullPath` resolves it against the server's working directory rather than
    throwing, producing a stable synthetic key. `PathResolver.GetContext` then falls through
    to `ForWorkspace(containingDirectory)`, and open documents never reach the cache because
    only `WorkspaceIndexer` commits records. That is exactly what hardening item 11 asked
    for — Workspace context, keyed off the URI, never persisted — satisfied by construction.
    Pinned by `Handlers/UntitledDocumentTests` so it cannot regress silently. A "proper" fix
    would have meant threading URI scheme through 13 call sites plus `DocumentStore` and the
    resolver, for no behavioural gain.
14. ✔ `workspace/didChangeWorkspaceFolders` (P14 #11) — `Handlers/WorkspaceFoldersHandler.cs`.
15. ✔ Rename fixes `#using`/`#insert` paths (P14 #12) — but NOT via
    `workspace/willRenameFiles`. **OmniSharp 0.19.9 models the LSP `FileRename` with a single
    `Uri` property; the spec's `oldUri`/`newUri` pair is absent**, verified by reflecting the
    real types — `WillRenameFileParams.Files` and `DidRenameFileParams.Files` are both
    `Container<FileRename>`, and `FileRename` exposes only `Uri`. A server-side handler
    therefore cannot learn a rename's destination. This is the "OmniSharp staleness" risk the
    plan accepted, hitting for the first time.

    Implemented instead as a custom `gscode/planRename` request: the client sources the event
    from `vscode.workspace.onWillRenameFiles` (which has both URIs) and defers the rename with
    `waitUntil` while the server plans the edits. All path reasoning stays server-side in
    `Resolution/DependencyRewrite.cs`, where the database lives, so only the transport differs
    from the original design. If OmniSharp ever models `FileRename` correctly, the handler can
    move server-side with no change to the planner.

**Wave 6 — P13 unblocked items.**
16. ✔ `completion.fieldScope` owner/all (P13 #2). Also closed the `GlobalObjectOwnersTests`
    porting row, since owner scoping only means anything once fields aggregate across files.
    Note the deliberate asymmetry: an unknown owner (`players[0].`, a call result) WIDENS rather
    than narrows, because offering nothing is worse than offering too much. Engine object fields
    and radiant keys are unaffected by the setting — it governs assignment-derived names only,
    per the plan.
17. ✔ Mined v1 `CodeActionHandler.cs` (P13 #3). Four actions ported, the rest dropped with
    reasons recorded in `tests/PORTING.md`. The decisive filter: a code action is only reachable
    if something reports the problem, so most of v1's ~20 fixes would have needed their
    diagnostic ported first — the portable set turned out to be exactly the diagnostics added in
    waves 1 and 3 (`UnusedUsing`, `PreferBooleanLiteral`) plus the existing
    `UsingAfterDeclaration`.
18. ✔ Corpus test category + `PerfTracker.Report` surfacing (P13 #5).
    `GSCode.Server.Tests/Corpus` runs the real `shareaw` tree behind `Category=Corpus`,
    excluded on CI and no-opping wherever the corpus is absent. `PerfTracker.Report` is now
    dumped after the memory breakdown (still `[Conditional]`, so normal builds pay nothing).

    **First run found real bugs, which is the point.** 980 scripts, zero crashes, formatter
    gates clean, 4 files with lex/parse errors (0.41%) — three distinct causes, below.

### P14 #19 — grammar gaps found by the corpus

**(a) Object-like macro invoked with `()`** — `gib.gsc(58)` / `gib.csc(35)`,
"Expected ';' but found '('".

`gib.gsh` declares `#define GET_GIB_BUNDLES struct::get_script_bundles("gibcharacterdef")`
(object-like, no parameter list), and the call site writes `GET_GIB_BUNDLES()`. Expansion
therefore yields `struct::get_script_bundles("gibcharacterdef")()` — a call applied to a call
result, which the parser rejects. Since this is shipped Treyarch code, the engine evidently
accepts it. Likely fix: let `ParsePostfixChain` accept `(` as well as `[` and `.`, which
generalizes the call-result indexing fix already made for
`players[q] getplayerangles()[1]`.

**(b) `&` applied to a macro that expands to a string** — `_quadtank.gsc(1741)`,
"Expected an expression but found '\"tag_target_lower\"'".

`#define WEAKSPOT_BONE_NAME "tag_target_lower"`, used as
`... triggerWeakpointDamage( &WEAKSPOT_BONE_NAME )`. Written directly, `&"tag_target_lower"`
lexes as a single localized-string (istring) token. Arriving via expansion, the `&` and the
string are separate tokens, and the parser's prefix `&` expects a function name. Fix: accept
`&` followed by a string literal as an istring in the expression grammar, not only in the
lexer.

**(c) Unmatched `#/`** — `vehicle_shared.gsc(3932)`, "Expected a function, class, or directive
but found '#/'".

Dev-block markers in that file look genuinely unbalanced (`/#` at 3244 and 3287 close at 3264
and 3292; the `#/` at 3932 has no opener above it). This may be a defect in the stock script
rather than a gap on our side, in which case our diagnostic is correct and the file is simply
one Treyarch never compiled with a strict parser. Needs confirming before changing anything —
do not "fix" the parser to accept unbalanced markers.

**Wave 7 — cleanup and low priority. ✔ DONE.**
19. ✔ Oversized-file guard (P14 #14). Smaller than the plan implied: v1's 65,535 cap existed
    because it packed positions into narrow fields, but v2's `Position` holds ints, so there is
    no correctness cliff. The 8 MB guard in `WorkspaceIndexer` only stops a pathological file
    dominating a cold index; the count surfaces via `IndexOutcome.SkippedOversized`.
20. ✔ `positionEncoding: utf-16` declared, plus `Text/SurrogatePairPositionTests`. Behaviour was
    already correct — `SourceText` indexes UTF-16 code units throughout — so this was
    declaration and proof, not repair.
21. ✔ `FOLDER.md` layout ratified per-project in `ARCHITECTURE.md` (drift turned into a
    documented decision) and all 12 real gaps filled, including a stray duplicate empty heading.
    `PORTING.md` resolved: every row closed except the two below.

### P14 #18 — macro expansion preview (the last porting-ledger row)

`MacroCallSiteModeTests` is the only `PORTING.md` row still open. Hovering a macro renders its
`#define` signature and doc comment but not what it expands to, which is the thing a caller
most wants to see for something like `IS_TRUE` or `NEW_STATE`.

**Blocked on data, not presentation:** `MacroRecord` deliberately carries no body — the
comment in `ScriptRecord.cs` reads "bodies stay parser-side" — so `MarkdownDocRenderer.RenderMacro`
has nothing to render. The work is: carry the body (or a rendered preview of it) onto
`MacroRecord`, decide how to present a multi-statement body sensibly, then render it. Watch the
memory implications: macro bodies are retained per record, and headers are inserted by hundreds
of files, so bodies should live once in the GSH record rather than be copied into every
importer.

**Blocked / user-gated, not scheduled:** PERF.md memory number (needs a BO3 machine),
`apiUpdate.ts` (needs the gscode.net endpoint format), headless CLI (ships only if wanted).

---

## P13 — optional / externally blocked

Items parked because they need something outside the repo, or are pure opt-in polish.

| # | Item | Why deferred |
|---|---|---|
| 1 | `apiUpdate.ts` opt-in online API refresh (`gscode.apiUpdate.enabled`) | BLOCKED: needs the gscode.net endpoint format + a server-side API-override hook. Setting is not published in package.json until it does something |
| 2 | `completion.fieldScope` (`owner` \| `all`) | Owner-scoped completion works; the `all` widening + the setting are unshipped |
| 3 | Mine v1 `CodeActionHandler.cs` (1,081 lines) via git history | Record keep/drop decisions in `tests/PORTING.md` |
| 4 | PERF.md steady-state memory number | Needs a real BO3 machine + a `GSCODE_INSTRUMENTATION` build |
| 5 | `PerfTracker.Report` surfacing + TA_TOOLS_PATH corpus test category | Corpus tests need a real `share\raw`; category auto-skips in CI |
| 6 | Headless CLI (`gscode check` / `gscode format --check`) | The plan's original P13. Ships only if wanted; nothing depends on it |

---

## P14 — plan-promised, unimplemented

Everything below was committed to in the plan (several user-confirmed as carries) and is
genuinely absent. Ordered by user-visible impact.

### Diagnostics model (highest impact — blocks three promises at once)

1. **`Diagnostic` has no `Tags` / `RelatedInformation`** — *Missing*.
   `Core/Diagnostics/Diagnostic.cs` is `(Range, Severity, Code, Message)`. Until it grows
   these fields, none of the following can be expressed. This is hardening item 23:
   - **Inactive `#if`/`#elif`/`#else` branch grey-out** via `DiagnosticTag.Unnecessary` —
     the plan names this as *the* grey-out mechanism the preprocessor section promises.
     The preprocessor already tracks excluded branches, so only the reporting is absent.
   - **Duplicate-definition `relatedInformation`** pointing at the first definition.
   - **Unused `#using` tagged `Unnecessary`** (pairs with the shipped remove-duplicate fix).

2. **prefer-boolean-literal lint** — *Missing*. User-confirmed carry (P4); no diagnostic
   code allocated, no analyzer. Warns on `1`/`0` where `true`/`false` is meant.

3. **`private` function diagnostic** — *Partial*. `DatabaseQueries` already filters private
   functions to their defining file (resolution/completion/lens are correct), but a
   cross-file caller gets silence instead of an "is private" diagnostic.

4. **ReadOnly-field write diagnostic** — *Missing*. `t7_object_fields.json` carries
   `readonly` flags and `ObjectFields` loads them, but writing one is not reported.

5. **`.size` write diagnostic** — *Missing*. `.size` is readonly; assigning to it is silent.
   (Tracked in `tests/PORTING.md` under `StringSizeAndBreakTests`.)

### Dead data — generated, loaded, never surfaced

6. **Radiant map-entity keys** — *Dead*. `t7_radiant_keys.json` (424 keys with comments as
   hover docs) is generated by `field-data`, loaded into `ObjectFields`, and exposed via
   `FindRadiantKey` — with **zero production callers** (tests only). P7 promised these as
   hover + completion on map-entity KVP keys.
   **User decision (2026-07-20): wire it up — do NOT drop the artifact.**

### FlowTyper (P10 carries)

7. **`BuiltinEmulations` table** — *Missing*. User-confirmed carry, successor to v1
   `DFA/EmulatedFunctions.cs`. Emulates return types/behavior of key builtins for sharper
   inference.

8. **`isdefined` narrowing** — *Missing*. User-confirmed carry. FlowTyper has no narrowing
   at all (no `Narrow`/`IsDefined` logic in the walk).

9. **Branch-join convergence** — *Missing*. `ScrType.Join` exists as a lattice operation but
   is never called from the per-function walk, so branches don't converge. This is the
   blocker for the `CfaTests` / `TypeFlowConvergenceTests` rows in `tests/PORTING.md`.

### Workspace lifecycle

10. **`gscode/rawFolderWriteWarning`** — *Missing*, and the setting is **dead**:
    `gscode.rawFileWarningMode` is published in `package.json` with `off|stock|all`, but
    nothing implements the notification, and `t7_stock_scripts.txt` is only hashed for the
    server build identity — never loaded to power the `stock` level.
    **User decision (2026-07-20): implement it fully — do NOT unpublish the setting.**

11. **`workspace/didChangeWorkspaceFolders`** — *Missing* (hardening 7). RootConfig does not
    rebuild / re-index on folder add/remove; requires a restart today.

12. **`workspace/willRenameFiles`** — *Missing* (hardening 6). Renaming a script should offer
    a WorkspaceEdit fixing `#using`/`#insert` paths that pointed at the old name.

13. **Untitled documents** — *Missing* (hardening 11). No `untitled:` handling; unsaved
    buffers should get Workspace context keyed by URI and never reach the cache.

### Input robustness

14. **Encoding + size guards** — *Partial* (hardening 10). **ANSI fallback: DROPPED by the
    user (2026-07-20)** — GSC sources are UTF/plaintext within the human-language range, so
    bare `File.ReadAllText` (which already detects a BOM) is sufficient and the fallback
    would be dead complexity. The **oversized-file guard** is a separate, still-open
    concern (v1 capped ranges at 65,535 lines): a pathological file should skip analysis
    with a clear diagnostic rather than misbehave. Kept at low priority.

15. **`positionEncoding: utf-16` not declared at initialize** — *Partial* (hardening 22).
    Behavior is already correct (C# `string` indices *are* UTF-16 code units, so
    `SourceText`'s line map counts the right thing), so this is declaration + a
    surrogate-pair/emoji regression test only. Lowest priority in this file.

### Docs & test ledger

16. **`FOLDER.md` is per-project, not per-folder** — *Partial*. The plan promised every
    folder gets one; there are 6 project-level files (922 lines total) covering subfolders
    by `##` section instead. The consolidation is defensible, but there are real holes —
    e.g. `Database/DatabaseQueries.cs` in `GSCode.Workspace/FOLDER.md` is an empty heading.
    Decide: ratify per-project layout in the plan, or split. Either way, fill the gaps.

17. **`tests/PORTING.md` unresolved rows** (now down to one: MacroCallSiteModeTests) — DiagnosticsTests, SymbolTableTests,
    MemoryOptimizationTests, ScriptManagerNamespaceScopingTests, ScriptDependenciesReadyTests,
    ScriptReferencesSelectionEndTests, MacroCallSiteModeTests, StockScriptsTests,
    GlobalObjectOwnersTests. Each needs a port or a conscious drop with a reason.

---

## Audited and confirmed present (no action)

Recorded so a future audit doesn't re-derive them: CI workflow, `.editorconfig`,
`GameProfile`, `PerfTracker`, `NameTable` interning, SQLite two-gate versioning +
`busy_timeout` + legacy-cache cleanup, user-region folding, `$` token, literal references,
documentLink, type hierarchy, `gscode.showReferences` bridge, `gscode/indexing*`
notifications, trigger characters (completion + signature help), private-function
*resolution* filtering, formatter `FormattingOptions` honoring, and `KeywordDocs`
(the P7 item that prompted this audit — landed late, during P12).
