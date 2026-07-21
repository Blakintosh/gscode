# Post-rewrite follow-ups (P13 / P14)

P0–P12 are complete and green. This file tracks what deliberately did NOT ship in the
rewrite, split into the two remaining buckets. P14 was opened after a global audit of the
implementation against the original plan (2026-07-20) and holds every plan-promised item
found unimplemented.

Legend: **Missing** = no implementation at all · **Partial** = wired but incomplete ·
**Dead** = code/data exists but nothing consumes it.

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

**Wave 2 — no dead surfaces.** User-mandated; both are shipped-but-inert today.
5. `gscode.rawFileWarningMode`: load `t7_stock_scripts.txt`, implement the
   `gscode/rawFolderWriteWarning` notification + client handler (P14 #10).
6. Radiant map-entity keys wired to hover + completion (P14 #6).

**Wave 3 — diagnostics carries.** All user-confirmed carries from the plan.
7. prefer-boolean-literal lint (P14 #2).
8. Cross-file `private` function diagnostic (P14 #3).
9. ReadOnly-field write + `.size` write diagnostics (P14 #4, #5) — one commit, same shape.

**Wave 4 — FlowTyper carries.** Ordered so the lattice lands before its consumers.
10. Branch-join convergence — wire `ScrType.Join` into the walk (P14 #9). Unblocks the
    `CfaTests` / `TypeFlowConvergenceTests` porting rows.
11. `isdefined` narrowing (P14 #8).
12. `BuiltinEmulations` table (P14 #7).

**Wave 5 — workspace lifecycle.**
13. Untitled documents (P14 #13) — **verify first**: `PathUtil.NormalizeAbsolute` runs
    `Path.GetFullPath` on the URI's file-system path, which fabricates a CWD-relative path
    rather than throwing, so this may already work by accident. Confirm empirically before
    building anything.
14. `workspace/didChangeWorkspaceFolders` (P14 #11).
15. `workspace/willRenameFiles` (P14 #12).

**Wave 6 — P13 unblocked items.**
16. `completion.fieldScope` owner/all (P13 #2).
17. Mine v1 `CodeActionHandler.cs` for portable quick fixes (P13 #3).
18. Corpus test category + `PerfTracker.Report` surfacing (P13 #5).

**Wave 7 — cleanup and low priority.**
19. Oversized-file guard (P14 #14, reduced scope — ANSI fallback dropped).
20. `positionEncoding: utf-16` declaration + surrogate-pair test (P14 #15).
21. `FOLDER.md` decision + fill gaps (P14 #16); resolve `PORTING.md` rows (P14 #17).

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

17. **`tests/PORTING.md` has 9 unresolved ☐ rows** — DiagnosticsTests, SymbolTableTests,
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
