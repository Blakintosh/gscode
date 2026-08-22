---
name: simplify-pass
description: Run one consolidation-and-simplification pass over an area of GSCode — collapsing duplicated logic, deleting what nothing calls, unifying the words the code and the docs use, and flattening pipelines that grew extra callers. Use when the ask is "simplify", "consolidate", "this is getting hard to follow", or before a release where the surface has drifted. It covers what counts as duplication here, what must be left alone, and the verification a `[VC]` cleanup has to clear.
---

# One simplification pass

A pass takes **one area** — a folder, or one project's slice of a feature — and ends in one `[VC]`
commit. Passes over the whole repo at once are how a cleanup turns into a rewrite: the diff stops
being reviewable, and a behaviour change hides inside it.

The rule that governs everything below: **a simplification pass changes no behaviour.** Same
diagnostics on the same scripts, same completions, same formatting bytes. If a change would alter
output, it is a feature and belongs in its own commit with its own justification.

The measure of a pass is **how many places one future change has to land** — not lines. See
"Knowing the pass worked".

## Pick the area first

Say the area out loud before reading code, because the area decides what counts as duplication.
Two handlers repeating four lines is duplication. `GSCode.Parser` and `GSCode.Server` repeating
four lines usually is not — see the layering rule below.

Good areas, in the order they tend to pay:

1. `GSCode.Server/Handlers` — 36 files, thin by design, and the place copies breed because each
   handler is written against the protocol on its own.
2. `GSCode.Workspace/Analysis` — 29 lint files that each answer a question about the same AST.
3. Any file over ~700 lines (`FlowTyper`, `Preprocessor`, `CodeActionHandler`, `GscFormatter`,
   `DatabaseQueries`, `SymbolExtractor`, `CompletionEngine.Producers`). Size alone is not a defect;
   it is a place to look.
4. The docs set: `ARCHITECTURE.md`, `FOLLOWUPS.md`, `FORMATTING.md`, `GAME_PROFILES.md`, `PERF.md`,
   and the seven `FOLDER.md` files.

**Read the area's recent history before reading its code:**

```bash
git log --oneline -15 -- server/src/GSCode.Server/Handlers
```

Cleanup passes leave the obvious wins taken. Proposing work a `[VC]` commit already did spends the
review twice and makes the whole findings list look untrustworthy.

## The four kinds of thing a pass collapses

### 1. Duplicated logic

Two or more sites computing the same answer. The bar for merging them is not "the code looks
alike" — it is **the two sites must be wrong together**. If a future change to one would have to be
mirrored in the other to keep the server correct, they are one thing written twice. If the two
could reasonably diverge, leave them.

Where the shared thing goes:

- Both callers in one project → a helper beside them, or a small dedicated type when it also holds
  the state the callers were each assembling (`Handlers/DocumentLinter.cs`, `Formatting/FormattingSupport.cs`).
- Callers in different projects → the **lower** of the two layers, and only if the thing is
  genuinely neutral there. `TextRange.Overlaps` earned its place in `GSCode.Core` because an
  inclusive range overlap test is a text fact, not a handler fact. `DocumentStore.TryGetAnalyzed`
  earned its place in `GSCode.Workspace` because "does this open document have a parse yet" is a
  question about the store. A helper that knows what a handler wants does not go in Core no matter
  how many handlers want it.
- **Never** the other direction. `Core → Parser → Workspace → Server` is one-way, and deduping by
  making a lower layer reference a higher one is not a simplification, it is a cycle waiting for a
  build error.

A merge that also **sheds constructor parameters** is the strong version: `DependentDiagnosticsRefresher`
went from six to three because four of them were used on one line. Count the parameters before and
after; that number moving down is the signal the pipeline really did get one owner.

When the new shared member is cheaper than the one it replaces in one direction and richer in
another, **say so in its own doc comment**. `TryGetAnalyzed` names itself the CHEAP resolve so the
next reader does not route a per-keystroke handler through `NavigationSupport.Resolve`, which also
builds the store, context id and declared namespaces. A shared thing that gets adopted where it
does not belong is worse than the duplication it removed.

### 2. Code nothing calls

Delete it. Nullable and warnings-as-errors are on, so the build catches unused *private* members —
which is exactly why the ones that survive are `public`. The build will never tell you about those.

Confirm before deleting, every time:

```bash
grep -rn "SymbolName" server/src server/tests server/tools client/src --include=*.cs --include=*.ts
```

A hit only in the file that defines it, plus a `FOLDER.md` line, means it is dead. Delete both.
`InitializationOptionsReader` sat unreferenced behind `public` until someone grepped.

**Unused `using` directives are in the same blind spot.** IDE0005 is not an error here, so removing
the last reference to a namespace leaves its import behind silently and the build stays green. When
a change drops the last use of a type — a `GameProfile` ternary, say — grep the file for anything
else from that namespace before leaving the import in place.

Also delete: comments that contradict the code, and second copies of a paragraph. Two stacked
comment blocks that disagree are worse than no comment, because a reader believes the first one.

### 3. The words

Four surfaces, one vocabulary. Fix drift in all four when the area touches them.

- **Type and method names.** The test is whether one concept has one name across the layers it
  crosses. Where it does not, the winner is the name the *lowest* layer uses, because that is the
  one the most callers already say. Rename with the LSP's own rename if the server is running,
  otherwise a scoped `grep` and a per-project build.
- **Fully qualified names.** `OmniSharp.Extensions.LanguageServer.Protocol.Models.Range` written out
  twelve times in one file is the same defect as duplicated logic: the reader re-parses the same
  string instead of recognising a name. Alias it the way `LspMapping` already does (`LspRange`,
  `LspSymbolKind`, `LspDiagnostic`). Before importing a whole namespace to avoid the aliases,
  **find a file that already imports both** — `SelectionRangeHandler` imports
  `GSCode.Parser.Syntax.Ast` and `Protocol.Models` together, which is proof they do not collide.
  Existing code is the cheapest collision test available.
- **Diagnostic messages.** All of them live in `Diagnostics/DiagnosticMessages.cs`, one table, so
  wording drift is visible by reading it top to bottom. House voice: full sentence, terminating
  period, source text quoted in `'single quotes'`, the offending thing named before the rule
  (`"Macro '{0}' takes {1} argument(s) but {2} were passed."`). Never start with "Error:" — severity
  is a field. Changing a message is a user-visible change: check no test asserts the old string.
- **Doc prose.** State the fact and the reason it is a fact. No hedging, no phase numbers repeated
  between documents — a fact written twice goes stale in one of them, and `ARCHITECTURE.md` says
  so about itself. A number that nothing checks is the specific failure to hunt for.
- **`FOLDER.md`.** One per project, and it is part of the diff, not a follow-up. Every symbol the
  pass adds, moves or deletes changes a line here. A pass that leaves `FOLDER.md` describing a
  deleted type has made the codebase harder to read, not easier.

### 4. Shape

Pipelines with more than one entry point, wrappers that only forward, and a call sequence written
out at three call sites including the comment explaining the step that corrupts files when skipped.
Give each pipeline **one caller-facing entry point** and let the steps live behind it. `OptionsFrom`
wrappers over `FormatOptions.From` were pure forwarding and went; `WorkspaceLints.Analyze` is the
one entry point for lints and stayed the one entry point.

The inverse trap: do not add a layer to remove a repetition. If the abstraction that unifies two
call sites needs a strategy parameter to tell them apart, the two sites were not the same site.

## What a pass must not touch

- **`GameProfile` and anything dialect-shaped.** Two profiles agreeing today is not duplication —
  it is two games that happen to agree, and merging them deletes the seam that makes the next game
  cheap. Read the `add-game-profile` and `gsc-dialect-facts` skills before touching anything with a
  profile in its signature.
- **Generated data** under `GSCode.Workspace/Api`. `regenerate-game-data` owns those files; editing
  them by hand is overwritten at the next regeneration.
- **Measured hot paths** — the lexer, preprocessor and parser inner loops. `PERF.md` holds the
  numbers. An indirection added there to save six lines costs time on every file in a 5,300-script
  index, and the pass has no measurement to justify it.
- **Deliberate repetition in tests.** Explicit setup repeated across test methods is legibility.
  A test you have to jump twice to read has been made worse.
- **Anything mid-flight in `FOLLOWUPS.md`.** Consolidating code that a listed follow-up is about to
  rewrite spends the review twice.

When you decline a merge that looks obvious, **leave the note at the site**: one line saying why the
on-type filter stays a line-level test rather than becoming `TextRange.Overlaps`. Otherwise the next
pass rediscovers the same false lead and has to re-derive the answer.

## Bugs the pass finds but does not fix

A survey reads more carefully than anyone has in months, so it turns up real defects. Those are
**not** part of the pass — fixing behaviour inside a no-behaviour-change commit is exactly what
makes such a commit unreviewable.

Report them in their own section of the findings list, with the evidence and the reason they are out
of scope, and leave the code alone. Example from the `Handlers` pass:
`PrepareRenameHandler` returns its registration options with no `DocumentSelector` while every
sibling sets one — likely registered against all documents rather than GSC files. Separate commit,
separate verification.

## The loop

1. **Survey the area read-only.** Read the tightly-coupled families in full (the navigation
   handlers, the diagnostics publishers); grep the rest for the shapes you already found. Produce a
   ranked findings list before editing anything.
2. **Show the list and get the subset.** Do not apply a survey. The findings list is the artifact
   the user prunes, so each finding carries what the user needs to prune it: `file:line`, the exact
   count and the command that produced it, the concrete shape of the resulting code, and which
   category it is in (must-be-wrong-together, or legibility only). Those are different kinds of
   change and a commit that mixes them should say so.
3. **Apply, one finding at a time**, shared member first and its callers after, so no edit lands
   against a helper that does not exist yet.
4. **Build after each finding**, not at the end — a per-project Release build is seconds, and a
   broken build after nine stacked edits costs the bisect. Build **every project the finding
   touches**: a member added in `GSCode.Workspace` and used in `GSCode.Server` needs both.
5. **Verify** (below).
6. **Commit** once, `[VC]`, per-file bullets.

**Zero findings is a valid outcome.** Report it and stop. The diagnostics-publishing family came
back clean because three earlier commits had already given each piece one owner; a survey that finds
something everywhere is usually finding nothing.

## Stop rules

Abandon a finding rather than force it:

- **The collapse needs a workaround to compile.** A cast, a suppression, a `!` — weigh it honestly.
  One cast with a comment explaining it (the `FoldingRangeKind?` switch arm) is acceptable when the
  result is one construction instead of three. A second workaround on the same finding means the
  two sites were not the same site.
- **A test fails after an edit.** Revert that finding and move on; do not fix forward. The pass is
  supposed to change nothing, so a red test means the premise was wrong, not that the test is.
- **The finding grew a behaviour change.** Back it out and file it in the section above.

## The verification gate

Per project, Release, never solution-wide — a running server holds the Debug DLLs open:

```bash
cd server
dotnet build src/GSCode.Server/GSCode.Server.csproj -c Release --nologo
dotnet test  tests/GSCode.Server.Tests/GSCode.Server.Tests.csproj -c Release --nologo --filter "Category!=Corpus&Category!=Perf"
```

Build the projects the diff touches, and run the suite that covers them (`Parser.Tests` for
lexer/preprocessor/parser/extraction, `Workspace.Tests` for resolution/database/completion/lints/typing,
`Server.Tests` for handlers and formatting).

**The corpus trigger is a path test, not a judgement call.** Sweep when the diff touches any file
under `GSCode.Workspace/Analysis`, `GSCode.Server/Formatting`, or `Diagnostics/DiagnosticMessages.cs`
— even when the change within that file is only a guard, because "this edit cannot move a byte" is
exactly the belief the sweep exists to check. cod4 and bo3 are the two that matter:

```powershell
$env:GSCODE_CORPUS_COD4='...\raw'; $env:GSCODE_CORPUS_BO3='...\share\raw'
dotnet test tests\GSCode.Server.Tests\GSCode.Server.Tests.csproj -c Release --nologo --filter "Category=Corpus"
```

Read the **duration**. An absent corpus makes every corpus test no-op and pass in milliseconds,
which looks exactly like success and proves nothing. Two games is roughly two and a half minutes;
anything under ten seconds did not happen.

If the client changed: `npm run compile` and `npm run lint` in `client/`, where the bar is zero
errors — the naming-convention warnings are pre-existing.

Report the gate as a table of what ran and what it said, including the durations. "Tests pass" is
not a result anyone can check.

## The commit

`[VC]` title saying what the code gained or lost in plain words, a short paragraph on the condition
that made it worth doing, then one bullet per file in diff order — shared members first, their
callers after, `FOLDER.md` last. No Claude co-author trailer.

Every count in the message must come from a command you ran, not from memory. "Twelve times, three
of them inside one declaration" is checkable and was checked; a remembered number in a commit
message is the same defect as a number in a doc that nothing checks.

Three exemplars, in increasing size — read one before writing yours:

```
git show 7d9eaf59   # a shared helper, two private copies dropped, nine literals folded into a factory
git show 6acec501   # two pipelines given one entry point each, six constructor params down to three
git show 25c2251e   # three shared members across two projects, both FOLDER.md files in the diff
```

The title says the outcome, not the activity. "Give TextRange the overlap test two handlers each
wrote for themselves", not "Refactor TextRange". The bullets say what changed *and why it was safe*:
"IsPreferred is a plain bool, so the three callers that left it unset were already getting false" is
the sentence that lets a reviewer stop checking.

## House style, non-negotiable

Enforced by `server/.editorconfig`, and a pass is the wrong place to relitigate any of it:

- Allman braces everywhere, braces even on one-line bodies.
- Padded control-flow parens: `if ( target is null )`.
- Explicit types over `var`, every declaration.
- No expression-bodied methods.
- Simple explicit logic over clever expressions; no tuple deconstruction.

## Knowing the pass worked

**The metric is sites, not lines.** State it as a before/after count of the places one future change
has to land. The `Handlers` pass went from 5 copies of the document guard, 2 directive resolvers and
4 hand-built Locations to one of each — while adding 26 net lines of code and 36 of comment, because
three documented shared members cost more lines than the five-line blocks they replaced. A pass that
optimises line count will refuse to write the doc comment that stops the next reader misusing the
thing it just created.

Then check:

- Every `FOLDER.md` line matches the code it describes.
- The unit suite for the touched projects is green, with a duration that says it ran.
- A reviewer can read the bullets and check the diff without opening a third file.
- Any count in the commit message was produced by a command.

Failure modes, in the order they actually happen: the pass grew a behaviour change; the pass merged
two dialect branches that were allowed to differ; the pass added an abstraction with a mode flag;
`FOLDER.md` was left describing the old shape; a stale `using` survived a change the build could not
see; the corpus was "run" in 400 milliseconds.

## Known limitations

- The pass is blind to duplication expressed differently in each copy — same answer computed by a
  loop here and LINQ there. Grep finds shapes, not meanings, so those survive until someone reads
  both files.
- "Must be wrong together" is a judgement, and two people will draw it differently on the same pair.
  The findings list exists so that judgement is the user's, not the surveyor's.
- Test-only duplication is deliberately out of scope, which means the suites drift on their own
  schedule.
