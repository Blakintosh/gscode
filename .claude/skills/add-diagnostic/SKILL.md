---
name: add-diagnostic
description: Add a new GSCode diagnostic code (gscode-NNNN). Use when introducing any new error, warning or hint — it covers the code ranges, the message table, where the rule belongs, severity choice, and the corpus check that has to pass before it ships.
---

# Adding a diagnostic

## 1. Pick a code, and check it is free

`src/GSCode.Core/Diagnostics/GscDiagnosticCode.cs`, banded by the layer that raises it:

| Band | Layer |
|---|---|
| 1xxx | lexing |
| 2xxx | preprocessing |
| 3xxx | parsing |
| 4xxx | extraction / per-file semantics |
| 5xxx | cross-file / workspace |

**The bands are not densely packed and the enum is not sorted by value.** Appending to the end of a
band's visual block is how you collide with a code defined further down the file. Check first:

```bash
grep -oE "= 50[0-9][0-9]" src/GSCode.Core/Diagnostics/GscDiagnosticCode.cs | sort -u
```

## 2. Add the message

`src/GSCode.Core/Diagnostics/DiagnosticMessages.cs`. Every code has a template, in one table, so a
code cannot ship without a message.

Name the mistake, not the token. `"Expected ';' but found '='"` makes the reader work out what is
wrong; `"Cannot assign to 'true' — assignment needs a variable, field or array element on the
left"` tells them. If the message would read the same for two different causes, that is a sign
they want two codes — see `5013`/`5014`, split precisely so the builtin half could be used as a
data source.

## 3. Put the rule where its evidence is

- **Per-file, syntax-only** → the parser (`Parser.*.cs`) or `SymbolExtractor`.
- **Needs the workspace** — other files, the index, the builtin library → a lint in
  `src/GSCode.Workspace/Analysis/`, registered in `WorkspaceLints.LintsOnly`.

Registering it in `WorkspaceLints` is what makes an offline corpus sweep meaningful: the sweep runs
the same list the editor runs, so a rule audited there is the rule users get.

**Two entry shapes.** A rule whose judgement is about ONE node — look at it, decide, move on, with
nothing threaded down the descent — exposes `InspectNode(node, …, diagnostics)` and is called from
`NodeLintPass.Run`, which asks nine rules per node in a single walk. It has no `Analyze` of its own:
a second walker only tests call is how the two silently drift. Test it through `NodeLintHarness`. A
rule that needs per-function state, a flag threaded down, or a cache carried along exposes
`static Analyze(...)`, walks the tree itself, and is called from `WorkspaceLints` directly.
`NodeLintPass`'s doc comment lists which rules are out and why; read it before choosing.

A cross-file rule is tested against `TestWorkspace.Build(files, profile?)` from `GSCode.Testing`,
with the asking file from `workspace.Analyze(relativePath, text)`. Name a game only when the rule
behaves differently on it — see the `standard-tests` skill.

**Decide what a macro-expanded reference means to your rule.** A reference with `FromMacro` set came
from a macro body, and its range is the INVOCATION site. Most call-site rules still report it — a
private or dev-only call a macro expands to breaks the build exactly as a written one does — and
`MacroReports.ShouldReport` is the one rule six lints share for that, including reporting one
invocation once. Use it rather than writing a seventh.

**An unreadable import usually means stand down.** `ImportGate` is the shared precondition: an
unresolved `#insert` takes its macros with it (an unexpanded macro reads as a call to nothing — one
missing `shared.gsh` once produced forty false reports), and an unresolved `#using` hides a merge
dialect's unqualified functions. Take it when your rule says "this name matches nothing" or "is not
in scope". A rule that does not assert absence should not: the unused-import lints and the
ambiguous-call lint each used to go silent for a whole file over one broken import.

## 4. Choose severity honestly

- **Error** — the script will not link or load. It has to be certain: an Error on working code
  trains people to ignore the panel.
- **Warning** — probably wrong, still runs.
- **Information** — optional to fix, but worth seeing in the panel. Only affordable when the corpus
  sweep says the count is small: `5015 UnreachableCode` is Information at 48 findings across all
  five games.
- **Hint** + the Unnecessary tag — dead or redundant code, in volume. Greys out rather than nags,
  and never reaches the Problems panel at all (see `5020 UnusedBinding`, 5,277 findings on BO3
  alone). A Hint WITHOUT the tag produces no visible output, so pair them or pick Information.

## 5. Gate it on what it actually needs

Several rules stand down rather than guess, and each condition was added because it fired wrongly
without it:

- `FunctionResolutionLint` needs `HasCompleteBuiltinLibrary`, a loaded library, **and** a finished
  index — before indexing completes every script function looks nonexistent.
- It also stands down entirely when a `#insert`/`#using` did not resolve: the set of legal names is
  unknowable then, so "matches nothing" is unsound.

If your rule can be wrong for a reason outside the user's control, gate it and say why in a comment.

## 6. Sweep the corpus before shipping it

Non-negotiable for an Error or Warning. ~5,300 shipped scripts across five games; anything reported
there is either a real defect in code that shipped and works, or a false positive in ours.

See the `build-and-test` skill for the environment variables and the duration check.

## 7. It has to fit inside the keystroke debounce

A new rule runs on every keystroke, inside `AnalysisTiming.DebounceMilliseconds` (250 ms), alongside
every rule already there. `LintBudgetTests` in the same `Category=Corpus` run asserts the bound:

- **no single rule over 40% of the debounce** on its worst file, bo3 or cod4
- **the whole pass under 25% at p99**

Nothing to add for a new rule — wrap its call in `WorkspaceLints` in a `using ( LintScope.For(...) )`
like its neighbours and it is measured. The failure names the rule, the milliseconds and the file.

Read the per-rule table it prints even when it passes: rules over a 20% WATCH line are called out
without failing, and `temp/gscode-lint-budget-<game>.html` names each rule's worst file. Measured
today, every rule sits at 9–17% of the debounce or below, so a new rule landing near the bound is an
outlier by an order of magnitude and worth a second look rather than a shrug. PERF.md's per-lint
budget section is where those numbers live.

A rule that cannot fit usually wants an INDEX rather than a faster walk — that is what took the
whole pass from 22 s to 1.4 s, and what `DeclarationIndex` and `NamespaceIndex` exist for.

**The budget gate runs at stock size, so it cannot see a rule that grows with the workspace.** A
rule that asks the store a question per call site can be 3 ms on bo3's 1,000 files and 300 ms at
50,000 — four rules were, until the scale sweep named them. Three habits keep a rule flat:

- Ask the narrowest index: `FilesDeclaring(namespace, name)` when the namespace is known, not the
  bare name. Never `AllRecords`.
- Pass `LookupFunctions` a `limit` when the rule only needs to know whether a name resolves (1) or
  resolves to exactly one declaration (2) — building thousands of `main`s to answer "yes" was two
  rules' whole cost on cod4.
- Skip the lookup when its answer cannot produce a finding: `PrivateAccessLint` stands down on a
  dialect with no `private`; `DevBlockCallLint` resolves only names `MayBeDevOnly` says could be.

Check it with `Category=Scale` (`GSCODE_SCALE_SIZES=10000,50000`, bo3 and cod4): the per-rule table it
prints must not climb between the two sizes.

## 8. Suppression comes for free

`WorkspaceLints.ApplyPragmas` filters the combined set, so `// #pragma disable NNNN` already works
for a new code — at any severity, including an Error. Do not add per-lint suppression.
