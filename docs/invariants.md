# Invariants — the rules that hold the design together

Each rule below was learned by breaking it. A change that breaks one is wrong even if every test
passes, unless it comes with a reason good enough to rewrite the rule here. Every rule names where it
is stated in more depth and, where one exists, the test that enforces it.

---

## Layering

**1. Dependencies point down: Core ← Parser ← Workspace ← Server.**
Core references nothing. Parser references Core. Workspace references both. Only Server references
OmniSharp.
*Why:* everything below the server can be tested with a string or an in-memory file system, and
the LSP library can change without touching analysis.
*Source:* `server/ARCHITECTURE.md`, "Projects and dependency flow". Enforced by the project
references in each `.csproj`.

**2. `LspMapping` is the only place Core types and protocol types meet.**
*Why:* one conversion means one place to get UTF-16 positions, severities and URIs right.
*Source:* `server/src/GSCode.Server/FOLDER.md`, `Mapping/LspMapping.cs`.

**3. The parser is a pure function.** `ScriptAnalysis.Analyze` does no I/O except through an injected
`IInsertProvider`, and gives the same output for the same input and profile.
*Why:* indexing runs it in parallel, tests run it on strings, and determinism is what makes the
cache safe.
*Source:* `server/src/GSCode.Parser/FOLDER.md`, introduction.

**4. Handlers are thin.** Logic lives in a Workspace query or engine; a handler resolves the
document, calls it, and maps the answer.
*Why:* the same question is often asked by several features (references, CodeLens, highlight), and
an answer computed in two places drifts.
*Source:* `server/ARCHITECTURE.md`, "Projects and dependency flow" and "Language features".

**4a. An answer that is a position must come from a fresh parse.** Completion, signature help,
semantic tokens and formatting use `NavigationSupport.ResolveFresh` / `DocumentStore.AnalyzeIfStale`;
an answer that is a symbol may use `Resolve`. Every read path that may re-analyse takes the
request's `CancellationToken`.
*Why:* analysis is 250 ms behind the keystroke, and a stale position lands on the wrong characters.
*Source:* `lsp-handler` skill, "Stale analysis is the recurring bug".

---

## One answer, one place

**5. `PathResolver` is the only authority on where a path leads.** Raw vs mod vs workspace, overlay
order, rejected paths. Nothing else probes the disk for a script path.
*Source:* `Workspace/FOLDER.md`, `Resolution/PathResolver.cs`.

**6. `PathUtil` is the only path normalizer.** Nothing else calls `Path.GetFullPath`. Database keys
are `PathUtil.NormalizeAbsolute`; directive paths are `RelativePathIndex.Normalize`.
*Why:* two spellings of one path compare unequal, and the symptom is an empty result, not an error.
*Source:* `Core/FOLDER.md`, `Paths/PathUtil.cs`.

**7. Shared questions have one query.** References, CodeLens counts and document highlight all go
through `NavigationSupport.FindAllReferences`. Prepare-rename and rename share
`RenameHandler.IsRenameable`. Every lint reaches the pipeline through `WorkspaceLints.Analyze`, and
the server reaches that only through `DocumentLinter`. What is a use of a local is decided once, by
`LocalUses.Of`.
*Why:* when two features compute the same answer separately, users see them disagree.
*Source:* the sections of those types in each `FOLDER.md`.

**8. `DatabaseQueries.LinkedScriptPaths` owns the `#using` / `#include` fork.** Callers do not
branch on the dialect to decide what a file links against.
*Source:* `Workspace/FOLDER.md`, `Database/DatabaseQueries.cs`.

---

## Isolation and visibility

**9. `.gsc` and `.csc` are two stores, not one store with a filter.** A query resolves the store
once from the asking file's language.
*Exception:* a **macro** key is looked up in both stores, because one header is inserted into both
worlds. Functions keep the isolation.
*Source:* `server/ARCHITECTURE.md`, "Language features". Tests: `MacroRenameAcrossLanguagesTests`,
`MacroReferenceScopeTests`.

**10. Visibility is by context.** Raw sees raw. A mod sees itself and raw. A workspace folder sees
the workspace folders and raw. Mods never see each other. A mod file shadows the raw file at the
same relative path. (`ScriptDatabase.CanSee`)
*Source:* `Workspace/FOLDER.md`, `Database/ScriptDatabase.cs`. Tests:
`OverlayShadowingReferenceTests`, `SameFileReferenceTests`.

**11. Never scope a field by the variable before the dot.** `self` is whatever a function was called
on; after `x = level;`, `x.` is `level`. Field completion offers every field and narrows by the typed
text only.
*Source:* `Workspace/FOLDER.md`, `Database/VocabularyIndex.cs`.

---

## Performance

**12. Nothing a keystroke or a request pays for walks every record.** New "every file that …"
questions get an index in `LanguageStore`, maintained in the same diff as the others
(`ApplyIndexes`). The remaining `AllRecords` walks are user-paced or one-off (startup reporting,
folder changes, the closed-file publisher and sweep, workspace symbol search); grep for
`.AllRecords` before adding another.
*Why:* at 50,000 files each such walk became a per-request cost growing with the workspace.
*Source:* `server/ARCHITECTURE.md`, `server/PERF.md` (scale section). Enforced by the `Scale`
suite's per-request budgets.

**13. No lint may take more than 40% of the 250 ms debounce on its worst stock file**, and the whole
pass no more than 25% at p99.
*Source:* `server/PERF.md`, per-lint budget. Enforced by `LintBudgetTests` (corpus).

**14. Measure before optimising, and record the result**, including what was tried and rejected.
*Source:* `server/PERF.md`. Some decisions there are final; check before re-proposing one.

---

## Correctness of results

**15. An Error never fires on code that ships and works.** Severity is chosen by sweeping every
supported game's stock scripts. A rule that cannot be certain stands down (`ImportGate`, the index
gate, profile capability gates).
*Source:* `add-diagnostic` skill; `server/src/GSCode.Workspace/FOLDER.md`, "Analysis/". Enforced by
`CorpusDiagnosticSweepTests` and `SampleScriptTests`.

**16. A lint is judged on what it catches, not only on what it does not report.** "Zero findings on
stock scripts" can mean the rule is useless. Before proposing a rule, show the mistakes it would
catch; every rule's test class pins cases where it fires as well as cases where it must not.
*Source:* review practice; not yet written into a skill.

**17. Unknown stays unknown.** The flow typer and every type-driven rule report nothing on
uncertainty: a union that is not exactly one type projects to `Unknown`, and `ScrOperators` answers
`Fine` when it cannot rule a shape out.
*Source:* `Core/FOLDER.md`, `Symbols/ScrType.cs`, `Symbols/ScrValue.cs`.

**18. The formatter cannot corrupt a file.** It refuses files with 1xxx or 3xxx errors, changes
whitespace only (plus the opt-in `fixCasing`), and re-lexes its output: a token stream that differs
from the input's means no edits.
*Source:* `server/FORMATTING.md`; `Server/FOLDER.md`, `Formatting/GscFormatter.cs`. Enforced by
`CorpusTests` and `GameCorpusTests` over every stock script.

**19. A superseded analysis neither publishes nor commits.** Diagnostics carry the version that was
analysed; `OpenDocument.Publish` refuses an older snapshot over a newer one; a buffer the editor owns
is never overwritten from disk.
*Source:* `Workspace/FOLDER.md`, `Documents/DocumentStore.cs`. Tests: `StaleAnalysisTests`,
`DiagnosticsPublishOrderTests`, `WorkspaceIndexerOwnershipTests`, `WatcherRaceTests`.

**20. The cache can never serve a stale record.** Content hash per file, schema and record-format
versions, and a build identity (game + assemblies + bundled data). Any mismatch wipes it; there are
no migrations. A new record field needs both halves of `RecordSerializer` and a version bump.
*Source:* `Workspace/FOLDER.md`, `Cache/`. Tests: `RecordSerializerTests`,
`RecordFormatCorpusTests`, `ServerBuildIdentityTests`.

---

## Dialects

**21. Every game difference goes through `GameProfile`.** No `if (game == "bo3")`. Flags derive
from the keyword set where they can, so they cannot drift from what the lexer does.
*Source:* `server/GAME_PROFILES.md`; `add-game-profile` skill. Tests: `GameProfileTests`.

**22. A core profile encodes nothing game-specific.** A blank is better than a guess.
*Source:* `server/GAME_PROFILES.md`.

**23. The server owns the game roster.** The client never keeps its own list of games.
*Source:* `client/src/FOLDER.md`, `gamePicker.ts`. Test: `SupportedGamesHandlerTests`.

---

## Process

**24. Open work lives only in `server/FOLLOWUPS.md`.** Not in code comments, not in other documents.
A count or phase number copied elsewhere becomes a second fact that drifts.
*Source:* `server/ARCHITECTURE.md`, "Known gaps".

**25. Documentation is updated with the code.** Each project's `FOLDER.md` has a section per source
file; a new file gets a section in the same change.
*Source:* `server/ARCHITECTURE.md`, "Documentation convention".

**26. Tracked text files are UTF-8 without a BOM and use LF line endings** (`.bat` files excepted,
which are CRLF).
*Source:* `.gitattributes`. Enforced by `SourceEncodingTests`.
