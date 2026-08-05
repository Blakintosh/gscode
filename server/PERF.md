# GSCode Performance

Budgets, methodology, and how to reproduce the perf pass. Numbers are gathered on a real
`share\raw` corpus (thousands of stock scripts) via a `GscodeInstrumentation` build; the results
table below is filled from a run on the local BO3-tools machine, since the corpus is not committed.
The measured tables are point-in-time snapshots, not release guarantees: rerun the perf category
after changing the analysis pipeline, cache identity, or indexing parallelism before comparing a
new result with these numbers.

## Budgets (targets)

| Scenario | Target | Notes |
|---|---|---|
| Cold index (no cache) | < 60 s | Full `share\raw` + all mods, bounded parallelism (cores − 1). |
| Warm start (cache hit) | < 5 s | SQLite restore of unchanged files; only changed files re-parse. |
| Steady-state memory | < 400 MB | Records-only retention for closed files; NameTable interning. |
| Keystroke re-analysis | interactive | Debounced ~250 ms, per-document cancellation; a single file lexes+parses in low single-digit ms. |

## Which corpora to sweep

**cod4 and bo3.** They cover both dialect shapes — cod4 is `#include` merge imports, TripleSlash
ScriptDoc and no headers; bo3 is `#using`/`#namespace`, `/@ @/` and the only game with `#insert`.
waw, mw2 and bo1 add roughly 6,400 files and about six minutes to the suite without adding a
distinct grammar shape, so they are swept only when a change is specific to one of them.

Their corpus roots are read from `GSCODE_CORPUS_<GAME>`. Those variables are usually set at the
USER level, so a child process inherits every game whether you want it or not — clear the three you
are not sweeping, or `GameCorpusFixture.Available()` will pick them up.

## Measured: where analysis time goes

`--filter "Category=Perf"` times every script in a game individually and splits each into the four
phases, writing `temp/gscode-perf-<game>.html` plus a `.json` sidecar, and rebuilding
`temp/gscode-perf-all.html` across every sidecar present. It is opted into rather than carried along
by the diagnostic sweep, since it costs a second pass over every file.

CoD4 is the control throughout: it has no `#insert` and 20 `#define` across 894 files, so its
preprocess figure is the FLOOR — the cost of walking every token to find nothing to do.

| | files | total | median | lex | preprocess | parse | extract |
|---|---:|---:|---:|---:|---:|---:|---:|
| cod4 | 894 | 1,556 ms | 0.17 ms | 41% | 15% | 30% | 14% |
| bo3 | 980 | 1,182 ms | 0.23 ms | 28% | 31% | 29% | 12% |

**These are not comparable with figures recorded before 2026-07-30.** The total used to be a
separate stopwatch around a second `Analyze()` call; it is now the SUM of the four phase timings.
The two used to contradict each other outright — one file reported 13.0 ms total against 0.2 ms of
phases, another 64.0 ms total against 74.1 ms — because a single-shot measurement at a sub-millisecond
median is dominated by whichever GC pause lands inside it. Deriving the total from the phases makes
them agree by construction, but it also means anything `ScriptAnalysis.Analyze` does AROUND the four
phases is no longer measured.

## Measured: the CROSS-FILE LINTS, which cost more than the parse

The table above times `ScriptAnalysis.Analyze` only. Everything the editor runs ON TOP of that
parse — the cross-file lints — went unmeasured until 2026-08-04, and turned out to be roughly
twenty times larger. `CorpusPerfTests.WorkspaceLints_WhereTheTimeGoes` times `WorkspaceLints.LintsOnly`
per file with the parse done outside the stopwatch, so it is lint cost and nothing else. It needs a
FINISHED index, unlike the parse sweep: two of the heaviest rules stand down without one, and timing
them against a partial index reports the cheap half as the total.

Four rules were 97% of it, and all four shared one cause — `DatabaseQueries.LookupFunctions` walked
every record and every function in each (~30,000 symbols on BO3) once per CALL SITE, so a file with
two hundred calls scanned the whole store two hundred times.

| | BO3, 980 files | CoD4, 894 files |
|---|---:|---:|
| before | 21,939 ms | 24,534 ms |
| + `DeclarationIndex` (name → declaring files) | 1,640 ms | 1,527 ms |
| + `FunctionLookupCache` (per-file memo) | 1,612 ms | 1,277 ms |
| after, three further runs | **1,398 – 2,098 ms** | **1,162 – 1,381 ms** |

Roughly 13x and 19x. **Only the first row and the last are separated by more than noise.** Three
runs of the finished code put BO3 anywhere in a 700 ms band, so the 1,640 → 1,612 step is not
evidence of anything, and CoD4's 1,527 → 1,277 sits at the edge of its own 220 ms band — suggestive
rather than established. The index is what did this; the cache's separate contribution is unproven
at this scale and would need per-file repetition to measure, which the sweep deliberately does not
do.

The distribution is both the stable half and the part that mattered, because `TextSyncHandler`
debounces at ~250 ms and the tail was over it. Across those three runs BO3's median held at
0.50–0.53 ms and CoD4's p99 at 10.6–12.3 ms, against totals swinging 50% and 19%:

| | before | after |
|---|---|---|
| BO3 median / p90 / p99 / max | 4.03 / 64 / 197 / 470 ms | 0.48 / 3.7 / 21 / 57 ms |
| CoD4 median / p90 / p99 / max | 3.45 / 68 / 343 / 611 ms | 0.56 / 3.6 / 11 / 45 ms |

CoD4's worst file, `scoutsniper.gsc`, fell from 611 ms to 45 — and its whole-corpus `max` now ranges
13–20 ms, so even the worst file is inside the debounce. The corpus sweep dropped from 5.9 minutes
to 2.8 as a side effect, and every game's per-code finding counts stayed byte-identical — the index
narrows WHERE to look and decides nothing.

Read the shares rather than the absolute milliseconds: the per-lint breakdown needs
`-p:GscodeInstrumentation=true`, which costs something itself, and this sweep is sequential where
the corpus one is parallel.

### How much to trust a single run

Three runs of identical code and methodology, cod4:

| | run 1 | run 2 | run 3 |
|---|---:|---:|---:|
| total | 1,483 ms | 1,650 ms | 1,837 ms |
| max | 49.3 ms | 55.8 ms | 51.9 ms |
| extract share | 17% | 16% | 16% |

**Shares are stable to about a point; absolutes swing by a quarter.** The noise is largely common to
all four phases, so it cancels in a ratio and does not in a total. Read shares, sub-phase ratios and
order-of-magnitude differences as real, and treat any single absolute number as indicative.

The practical consequence: this harness will find a structural problem — it caught a quadratic doc
lookup that was half of extraction — and will NOT settle a 10% constant-factor tune. Measuring one of
those needs repetition per file, which the sweep deliberately does not do.

The technique that does work at small scale is an **internal control**: instrument the path you are
changing and one you are not, then compare. A change that moves the target 7x while the control sits
within noise is real regardless of what the totals do.

BO3 used to sit at 60% preprocess and 5,562 ms, because its headers were re-read, re-lexed and
re-walked once per including file — 2,137 insert directives naming 114 headers, so about nineteen
times each. Two caches, both keyed by the RESOLVED path so a mod's header and the raw one it shadows
stay separate:

- `InsertCache` holds the lexed header. Took BO3 to 3,331 ms.
- `IHeaderMacroCache` holds what a header CONTRIBUTES — its definitions, in order, plus its nested
  insert edges — so the second file to insert it replays instead of walking. Took it to 1,369 ms.

Net 4.1x on BO3's analysis, and its phase profile now matches CoD4's rather than being bent around
preprocessing. CoD4 itself moved within noise, which is what a control with no headers should do.

Read these as ANALYSIS cost, not startup: the sweep is sequential and warms first. A VS Code index of
the same tree reports much larger per-file numbers because it runs at `ProcessorCount - 1` (so each
file's wall-clock includes contention), covers record construction and the database insert as well,
and is cold. Both are honest; they answer different questions.

## Sub-phases: inside `extract`

A four-way phase split says extraction is expensive; it does not say which part. Building with
`-p:GscodeInstrumentation=true` compiles in `PerfTracker` scopes and adds a per-file breakdown to
both the console output and the report. Without that flag every scope call and the snapshot that
reads them are `[Conditional]` and absent from the IL, and the report says "not instrumented"
rather than showing a blank table.

The scopes, and what a high figure means:

| scope | covers | reads as |
|---|---|---|
| `extract.declarations` | the whole declaration walk | parent of the two below; the bulk of extraction |
| `extract.body` | `WalkStatement` over one function | proportional to the code — the honest floor |
| `extract.doc` | doc-comment association for one declaration | should be flat per call; if it scales with file size, something is scanning |
| `extract.macros` | macro definitions and uses to references | proportional to `#define`/`#insert` use |
| `extract.duplicates` | the duplicate-function report | dictionary-keyed, should stay near zero |

### What this found

`FindDocComment` scanned the raw token stream from the top FOR EACH declaration. Both a file's
function count and its token count grow with its size, so the cost was **O(functions x tokens)** —
invisible on a median file and dominant on the largest. `maps\_utility.gsc` was the slowest file in
four of the five game corpora because it is the biggest AND the best documented: 163 of cod4's 262
doc blocks are in that one file, and only 8 of its 894 files contain any at all.

Replaced by an index built once per file, keyed by the line each doc comment ends on. Measured by
removing only the cache so the index rebuilds per declaration, restoring the old complexity with
every other line identical:

| | cod4 before | cod4 after | bo3 before | bo3 after |
|---|---:|---:|---:|---:|
| `extract.doc` | 337 ms | 49 ms | 296 ms | 10 ms |
| `extract.body` *(control)* | 198 ms | 219 ms | 111 ms | 109 ms |
| `extract.declarations` | 549 ms | 286 ms | 429 ms | 149 ms |
| extract share of phases | 29% | 16% | 31% | 12% |

The untouched control moving by ~10% while the target moves 7x and 30x is what makes this
attributable rather than luck. The raw ratios overstate the real win by about 2x, because the
original stopped scanning once past the declaration's line and so read half the stream on average
(`T x F / 2`, against `T x F` for the no-cache arm). Corrected, the doc lookup is roughly **3.4x
faster on cod4 and 15x on bo3**.

bo3 gains more because identifying its doc token is a `Kind` check, so nearly all of its old cost
was scan length, which the index removes outright. cod4 must fence-scan the TEXT of every block
comment to recognise `///ScriptDocBegin`, and still pays that once per file — which is why its
post-fix figure stays several times bo3's on a third fewer calls. If `extract.doc` is ever worth
attacking again on cod4, the fence scan is the target, not the lookup.

## What the server logs (no special build needed)

At `gscode.serverLogLevel = info`, the "GSCode Server" output channel logs the two headline
numbers directly:

- `Workspace indexing complete: N files in X.Xs (M from cache)` — the cold/warm index time (also
  mirrored to the client "GSCode" channel and the status-bar tooltip). The cache count makes a
  run self-identifying as cold or warm.
- `Memory after indexing:` — a one-shot breakdown at the indexing → serving transition (below).
  **Verbose only.**
- `Server memory: N MB` — the working set, sampled every 3 s but logged only when it moves by
  >= 1 MB, and only AFTER indexing completes (so it never spams while memory is climbing).
  **Verbose only.**

Both are gated by the log level rather than an environment variable, so `gscode.serverLogLevel =
verbose` is all it takes. The same sample feeds the status-bar tooltip, which shows the working
set without any log level at all — usually enough, and the reason the log lines can be quiet by
default.

### Reading the memory breakdown

An actual cold reading:

```
Memory after indexing:
    files              1,105  (0 restored · 1,105 analysed)
    working set        390.3 MB   (what the OS reports)
    managed live       116.6 MB   (retained objects)
    heap size          282.1 MB
    committed          303.9 MB
    fragmented         183.1 MB   (mostly large-object heap)
    collections     gen0 182 · gen1 100 · gen2 19
```

**The number that matters is the gap between "managed live" and "working set."**

Cold indexing allocates heavily per file — source text, token arrays, the PToken stream, the
AST, extraction builders — at `ProcessorCount - 1` way parallelism, and all of it is garbage
once the `ScriptRecord` is built. A warm start only deserializes records from SQLite, so it
never allocates at that scale.

### Measured answer (2026-07-20, 1,105 files)

| | Cold | Warm | Delta |
|---|---|---|---|
| managed live | 116.6 MB | 112.7 MB | **+3.9 MB** |
| fragmented | 183.1 MB | 1.0 MB | **+182.1 MB** |
| heap size | 282.1 MB | 100.9 MB | +181.2 MB |
| working set | 390.3 MB | 212.2 MB | +178.1 MB |
| gen2 collections | 19 | 8 | |

**The live object graph is the same** — 3.9 MB apart, about 3%. There is no leak and nothing
is over-retained; records-only retention works as designed. The entire gap is fragmentation:
182.1 MB of it explains 178.1 MB of working set, and a cold heap is 65% holes. Native/runtime
overhead is a constant across both (86 MB cold, 92 MB warm), as expected.

The cause is the large-object heap. A `PToken` is roughly 48 bytes, so the 85,000-byte LOH
threshold lands at about 1,770 tokens — which most real GSC files clear, meaning the majority
of scripts allocate their token arrays straight onto a heap that is never compacted by
default. Nineteen gen2 collections still left 183 MB fragmented, because ordinary collections
reclaim LOH memory without moving anything.

**Fix in place:** `CompactIfFragmented` in `Program.cs` runs one `CompactOnce` gen2 collect at
the indexing → serving transition, gated on measured fragmentation (32 MB) so a warm start
skips the pause. The report is logged again afterwards as "Memory after compaction", so any
run shows its own before/after. v1 reached the same conclusion in `cfccd26`, "Force aggressive
GC after workspace indexing completes".

Two things this measurement also settled:

- **Do not switch to Server GC.** Per-core heaps would multiply the fragmentation, and
  Workstation GC is the right choice for a language server.
- **Cache restore is not skipping `NameTable` interning.** That worry predicted a warm start
  carrying duplicate strings, which would show up as a *higher* warm live set. It came in 3.9
  MB *lower*, so the concern is closed.

If cold ever climbs again without fragmentation climbing with it, that is the leak-hunt
signal — a genuinely higher live set means the analysis path is retaining something.

## Reading the reports

Each game writes `temp/gscode-perf-<game>.html`; `GSCODE_PERF_REPORT` overrides the directory.

- **Headline stats, phases, sub-phases** — the run in summary.
- **Slowest by absolute time** (top 25) — where the wall-clock went.
- **Slowest per kilobyte** (top 25, files over 4 KB) — meant to catch superlinear behaviour. Treat
  it with suspicion at the small end: reconciling the total removed the contradiction between the
  two measurements but NOT the GC noise, so a 5 KB file can still reach the top of this table on a
  single unlucky collection. Corroborate against the phase columns before believing a row.
- **All files** — every row, sortable by any column and filterable by path. This is the actual data;
  the tables above are only the questions someone already thought to ask.

`temp/gscode-perf-all.html` is rebuilt from the JSON sidecars after every game, so it is correct
whether one game was swept or five, and independent of the order the two xUnit facts run in. It
carries each game's own run timestamp — a sidecar left behind by an earlier sweep shows as stale
rather than being silently folded in. Its "hotspots in more than one game" table matches on file
NAME across each game's slowest 50, since the lineage reuses script names; a file there is usually
one script evolved across releases, and fixing what it exercises pays out in every game at once.

## Deeper timing (optional instrumentation)

Per-stage timing scopes are compiled in only when the `GscodeInstrumentation` property is set, so
normal builds pay nothing (`Core/Instrumentation/PerfTracker.cs`,
`[Conditional("GSCODE_INSTRUMENTATION")]`):

```
dotnet build server/GSCode.slnx -c Release -p:GscodeInstrumentation=true
```

The indexer wraps its fan-out in an `index.total` scope, and `Program.cs` dumps
`PerfTracker.Report(...)` as `Perf  <scope>: N calls, X ms total, Y ms mean` right after the
memory breakdown. Both the scopes and the dump are `[Conditional]`, so a normal build pays
nothing for either.

## Corpus tests

Every class under `GSCode.Server.Tests/Corpus` shares ONE xUnit collection
(`GameProfileCollection`), which is what stops them running in parallel. `GameProfile.Active` is
process-global — the indexer enumerates through its script globs, the lexer gates keywords on it —
so two games swept at once means one analysed under the other's dialect. That is not hypothetical:
it once reported 861 of BO3's 980 scripts as unparseable, complaining that `function` and `#using`
were unknown. The constraint had been written in a comment for a long time and enforced by nothing.

The same global is why the games CANNOT be parallelised against each other. Within a game the
per-file loop does run in parallel, at `ProcessorCount - 1`, mirroring what `WorkspaceIndexer`
already does with the same pipeline; sweeps are also memoized per game, since four tests wanted the
same one. Those two took the sweep from 13 minutes to 5.9, and the lint indexing above took it to
2.8.

`GSCode.Server.Tests/Corpus` runs the real BO3 raw tree named by `GSCODE_CORPUS_BO3`, tagged
`Category=Corpus` and excluded on CI (which has no mod-tools install; the tests also no-op on
their own when the corpus is absent). Four checks: every script analyses without throwing,
lex/parse errors stay under 1% of the corpus, and the formatter's two property gates —
token-stream equality and idempotence — hold over a 250-file sample.

First run, 980 scripts: **zero crashes**, formatter gates clean, and **4 files with lex/parse
errors (0.41%)** — three distinct genuine grammar gaps. One (`&"..."` parsing as address-of
rather than an istring) was fixed, taking it to 3; the other two are consciously left and
recorded in `FOLLOWUPS.md`. The listing
in the test output is the real signal; the 1% budget exists only so one odd file cannot block
the suite.

## How to run the pass

0. For the per-file sweep: set `GSCODE_CORPUS_COD4` and `GSCODE_CORPUS_BO3`, CLEAR
   `GSCODE_CORPUS_{WAW,MW2,BO1}` (they are usually set at the user level and would otherwise be
   inherited), then `dotnet test --filter "Category=Perf"`. Add
   `-p:GscodeInstrumentation=true` to both the build and the test invocation for sub-phases.

The remaining steps measure the SERVER, which is a different question — the sweep is sequential and
in-process, while the server indexes cold at `ProcessorCount - 1`:

1. Point `gscode.rawPath` at the game's raw folder (and `gscode.modsPath` at its mods folder, to
   exercise overlay resolution), set `gscode.serverLogLevel = verbose` (the memory lines are
   Verbose) and `gscode.workspaceIndexingMode = full`.
2. Launch the extension against the tools root (see the client `.env` debug flow, or install the
   packaged extension).
3. For cold vs warm: delete `%APPDATA%\gscode\cache\*.db`, start once (cold), restart (warm), and
   read the "indexing complete" line each time.
4. Read the steady-state "Server memory" line once the process settles — or just hover the status
   bar, which shows the same number live.

## Results

Measured on the local BO3-tools machine (corpus not committed):

| Scenario | Corpus size | Measured | Budget | Within budget |
|---|---|---|---|---|
| Cold index | 1,105 files | 5.5 s | < 60 s | yes |
| Warm start | 1,105 files | 2.6 s | < 5 s | yes |
| Steady-state memory (cold, before compaction) | 1,105 files | 390.3 MB | < 400 MB | just inside |
| Steady-state memory (warm) | 1,105 files | 212.2 MB | < 400 MB | yes |
| Live managed set (either path) | 1,105 files | ~115 MB | — | — |
