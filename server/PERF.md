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

Two facts name **bo1** as well, and for the same reason both times: the quantity they measure scales
with corpus SIZE rather than with grammar, and bo1 is the only corpus large enough to show it.
`ColdIndex_WhereTheTimeGoes` wants its 160,382-file raw tree for enumeration cost;
`Completion_WhereTheTimeGoes` wants its 2,963 scripts because every query on that path reads the
record store. Adding a game to a sweep needs an argument of that shape — not a wish for more data.

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

Re-measured 2026-08-12, uninstrumented, with the instrumented run of the same code beside it so the
flag's own cost is visible rather than folded in:

| | files | total | median | lex | preprocess | parse | extract |
|---|---:|---:|---:|---:|---:|---:|---:|
| cod4 | 894 | 816 ms | 0.05 ms | 36% | 11% | 37% | 17% |
| cod4 *(instrumented)* | 894 | 899 ms | 0.04 ms | 32% | 10% | 41% | 16% |
| bo3 | 980 | 991 ms | 0.20 ms | 29% | 23% | 28% | 20% |
| bo3 *(instrumented)* | 980 | 980 ms | 0.19 ms | 27% | 20% | 35% | 18% |
| bo1 | 2,960 | 3,111 ms | 0.05 ms | 34% | 9% | 41% | 16% |

**Shares are stable to about a point only within a phase, not across a run.** The three-run cod4
table further down establishes that for `extract` and it does not generalise: `lex` and `parse` move
4–5 points between the two runs above, which are the same code minutes apart. So read a 4-point
difference as nothing. Two moves here survive both runs and are therefore real:

- **bo3 `extract` 12% → 18–20%**, against `preprocess` falling 31% → 20–23%. Extraction's share on
  the `#insert` dialect has roughly doubled since the figure above it was taken.
- cod4's total falling by half (1,556 → 816 ms) with its median falling 3x. The distribution is now
  sharply bimodal — median 0.05 ms against a p99 of 15.3 ms — so the median says little and the
  slowest 1% carry 24% of the total.

**These are not comparable with figures recorded before 2026-07-30.** The total used to be a
separate stopwatch around a second `Analyze()` call; it is now the SUM of the four phase timings.
The two used to contradict each other outright — one file reported 13.0 ms total against 0.2 ms of
phases, another 64.0 ms total against 74.1 ms — because a single-shot measurement at a sub-millisecond
median is dominated by whichever GC pause lands inside it. Deriving the total from the phases makes
them agree by construction, but it also means anything `ScriptAnalysis.Analyze` does AROUND the four
phases is no longer measured.

### 2026-08-16: lex is half what it was, and is no longer the largest phase

Three changes to the scan, all of them shape rather than algorithm — the position lookup resuming
from the last token's line instead of binary-searching per token, the character runs read through
`SearchValues<char>` instead of one character per iteration, and the dialect's keyword set answered
by hash instead of by reading all two dozen entries. Uninstrumented, one run each, before and after
in the same session on the same machine:

| | files | total | lex | lex share | parse share |
|---|---:|---:|---:|---:|---:|
| cod4 before | 894 | 825 ms | 284 ms | 34.4% | 42.5% |
| cod4 after | 894 | 764 ms | **144 ms** | **18.8%** | 49.3% |
| bo3 before | 980 | 944 ms | 303 ms | 32.1% | 31.6% |
| bo3 after | 980 | 612 ms | **121 ms** | **19.8%** | 30.3% |
| bo1 before | 2,960 | 3,018 ms | 1,034 ms | 34.3% | 40.7% |
| bo1 after | 2,960 | 2,306 ms | **443 ms** | **19.2%** | 47.6% |

**Read the share, not the total.** Lex falls by 49%, 60% and 57% and its share drops about fifteen
points on all three corpora, in the same direction and by nearly the same amount — that is the
result. The totals are not admissible on their own at this sample size, and the intended control
proves it rather than confirming anything: `parse` moved +7% on cod4 and −38% on bo3 between the
same two runs, which is the quarter-sized swing this file warns about further down and not an
effect of a change that never touched the parser.

Behaviour was pinned before the timings were taken, since a scanner that is faster and wrong is
worth nothing: every token's kind, offset, length and range was dumped for eleven hand-written
sources covering each changed path plus 4,000 seeded-random strings over a GSC alphabet, before and
after, 182,821 lines byte-identical. The corpus sweep then reported the same counts on both sides —
2 of 894 on cod4, 2 of 980 on bo3, 6 of 2,960 on bo1, with the formatter's gates clean.

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

### Re-measured 2026-08-12: the tail has doubled, and the type rules are why

The bands above no longer hold. Measured uninstrumented, with the instrumented run beside it —
which is *faster* on cod4, so the flag does not explain any of this:

| | band above | instrumented | uninstrumented |
|---|---:|---:|---:|
| cod4 total | 1,162 – 1,381 ms | 2,220 ms | **2,181 ms** |
| cod4 median / p99 / max | 0.56 / 10.6–12.3 / 45 ms | 0.72 / 24.2 / 64.7 ms | **0.75 / 23.5 / 40.4 ms** |
| bo3 total | 1,398 – 2,098 ms | 2,017 ms | **2,191 ms** |
| bo3 median / p99 / max | 0.50–0.53 / 21 / 57 ms | 0.56 / 19.8 / 54.0 ms | **0.61 / 23.1 / 95.1 ms** |

**cod4's p99 is twice its band and its total is 58% over.** bo3's total clears the top of its band
and its worst file reaches 95 ms against a previous 57. Medians are up about a quarter on both.

The per-lint scopes say where it went. Five rules are new since the bands were taken, all of them
reading the flow typer, and they are marked below:

| rule | bo3 (862 files) | cod4 (894 files) |
|---|---:|---:|
| `ArgumentCountLint` | 256 ms | 236 ms |
| `TypeMismatchLint` * | 243 ms | 225 ms |
| `UnusedIncludeLint` | 0 ms | 225 ms |
| `PreferBooleanLiteralLint` * | 192 ms | 142 ms |
| `ConstDeclarationLint` * | 169 ms | 176 ms |
| `DevBlockCallLint` | 138 ms | 179 ms |
| `ArithmeticLint` * | 105 ms | 86 ms |
| `ReadOnlyWriteLint` * | 105 ms | 58 ms |
| `IncludeUsageLint` | 0 ms | 114 ms |
| `FunctionResolutionLint` | 58 ms | 90 ms |
| **starred five, together** | **814 ms (40%)** | **687 ms (31%)** |

So a third to two fifths of all lint time is now the type rules. That is a cost, not a defect — they
are five diagnostics that did not exist — but it is four times what the lattice measurement further
down implies, and the reason is that the lattice was measured run-alone against a suite that warms
the process first.

`UnusedIncludeLint` and `IncludeUsageLint` costing 339 ms on cod4 and nothing on bo3 is the dialect
split showing in the profile: the merge dialect walks an include closure the namespace dialect never
builds. It is also why one corpus cannot stand in for the other here.

**Nothing about this is user-visible.** A 23 ms p99 is ten times inside the 250 ms debounce, and
bo3's 95 ms worst file — a single-shot measurement at that — is the closest anything comes. Do not
optimise on these numbers. They are here so that the next 2x has something to be a 2x *of*.

## Measured: COMPLETION, and why it is NOT worth optimising

`CorpusPerfTests.Completion_WhereTheTimeGoes` times `CompletionEngine.Complete` at ten evenly spaced
call sites per file, with a finished index, the parse done outside the stopwatch, and each position
warmed before the timed run. One row per REQUEST rather than per file, because the question is
whether one keystroke is answered in time and a per-file sum answers nothing anybody waits for.

It was written expecting to find the lint problem again. `FunctionsInNamespace` walks the whole
record store once **per namespace**, and on a namespace dialect statement-scope completion calls it
once per own namespace *plus* once per imported namespace — the same shape as the `LookupFunctions`
scan above, on a path with no debounce behind it.

The shape is real and it is visible in the numbers. It does not matter.

| | requests | median | p90 | p99 | max |
|---|---:|---:|---:|---:|---:|
| bo3 (1,085 files, **namespace** dialect) | 6,381 | 0.42 ms | 2.03 ms | **4.22 ms** | 24.2 ms |
| cod4 (904 files, merge dialect) | 6,944 | 0.20 ms | 0.34 ms | 1.03 ms | 16.4 ms |
| bo1 (2,963 files, merge dialect) | 21,157 | 0.44 ms | 0.64 ms | 1.47 ms | 31.4 ms |

Reconfirmed 2026-08-12 on all three, and it is the most stable measurement in this file: bo3
p99 4.26 ms, cod4 1.09 ms, bo1 1.57 ms, with the entry-count line identical to the digit. Nothing
here has moved while the lint sweep next door doubled its tail — worth knowing, since the two share
a database and an index.

**BO1 is the control, and it is the row that settles this.** Its store is 2.7x BO3's, and its p99 is
a third of BO3's. Store size is not what drives the cost — the per-namespace multiplier is, and BO3
is the only `ImportStyle.Namespace` profile. So the quadratic is exactly where it was predicted to
be, and a corpus nearly three times larger is *faster* because it takes the single-walk
`FunctionsInIncludeScope` arm instead.

The absolute numbers are why nothing was changed. A p99 of 4.22 ms sits some fifty times inside the
250 ms debounce, and two orders of magnitude inside human typing cadence. The lint fix was worth
13–19x because the tail was over the debounce; this tail is not close to it. An index here would be
correct, would measurably reduce a number, and would buy the user nothing.

**What would change the answer**, since this is a snapshot and not a guarantee:

- A namespace dialect with a much larger store. The multiplier is per imported namespace, so it
  grows with store size *and* with import count together — BO3's stock scripts are the only
  namespace corpus that exists, and a large mod is not bounded by them.
- Any new caller putting `FunctionsInNamespace` on a per-reference footing rather than a
  per-namespace one. That is the difference between this result and the lint one: same walk, an
  order of magnitude more of them.

If either happens, the fix is already designed — a `NamespaceIndex` on `LanguageStore` keyed on
`FunctionSymbol.Namespace`, built exactly like `DeclarationIndex`, plus a `RelativePathIndex` for
the three queries that scan every record to find the ~20 a `#using` list names. Re-run this sweep
first; the reason it exists is that the same reasoning predicted a problem here and was wrong about
the size of it.

### Reading the entry-count line

The sweep also reports how many entries came back, and that line is what makes the timings
admissible rather than decorative. `Complete` has around ten arms and most return almost nothing —
a path segment list, an asset type list, an empty result where the position was not a completion
site. Only the statement-scope arm queries the store.

At 66–74% of requests returning over 500 entries (median 847–1,404), the sample is landing on that
arm. A sweep that quietly hit cheap arms would report fast completions and have measured nothing,
which is the partial-index failure above arriving by a different route.

### 2026-08-12: the list grew by two thirds and the timings did not move

Statement scope gained the enclosing function's parameters and locals, the enclosing class's `var`
members, and every macro an `#insert`ed header supplies — the last of which had been filtered out.
Measured as a BEFORE/AFTER PAIR in one session on one machine, which is the only way this reads:
the absolute numbers in the table above were taken elsewhere and are not comparable to these.

| | entries median | entries max | p99 |
|---|---:|---:|---:|
| bo3 before | 1,168 | 5,059 | 6.01 ms |
| bo3 after | **1,930** | **5,937** | 6.03 ms |
| cod4 before | 847 | 2,580 | 1.28 ms |
| cod4 after | 847 | 2,592 | 1.41 ms |

**A 65% larger list for 0.02 ms at the tail.** The added work is per-request bounded — one walk of
the enclosing declaration's parameters and assignments, one inheritance walk for members, and a
table the preprocessor had already built — while the cost this path is made of is the per-namespace
store walk above, which did not change.

CoD4 is flat because the two dialects gain different things: a merge dialect has no `#insert`, so
the macro half is empty, and its scripts assign through `level.` rather than to locals — three
sampled functions in `animscripts\battlechatter.gsc` declare 12, 15 and 7 assignments and only 1, 1
and 2 of them are locals. Two entries per request is not a measurement. That the numbers barely
moved there is the expected answer, not a sign the feature is missing on that dialect.

## Measured: COLD INDEXING, the first-run path

`CorpusPerfTests.ColdIndex_WhereTheTimeGoes` times `IndexAsync` with **no cache attached**, so every
file is read and analysed and none is restored. Until 2026-08-04 nothing timed indexing at all — the
5.5 s figure further down was read by hand off the server's own log line, and every `IndexAsync` in
the test suite was setup, outside any stopwatch.

Three runs, uninstrumented:

| | files | wall-clock |
|---|---:|---|
| bo3 | 1,085 | 2,065 / 2,101 / 2,104 ms |
| cod4 | 904 | 898 / 855 / 807 ms |

**Stable to about 2%**, unlike the lint sweep next door — a cold index is thousands of files, so the
per-file noise that swamps a sub-millisecond median averages out. Comparisons here can be made on
one run; the lint numbers cannot.

The stage breakdown needs `-p:GscodeInstrumentation=true`. Per-file scopes are summed ACROSS
`ProcessorCount - 1` threads, so they are shares of thread-time, not of wall-clock; `index.enumerate`
is serial and is reported against wall-clock instead. `extract.*` are nested inside `index.analyse`
and are listed but never added to the total.

| stage | bo3 (of thread-time) | cod4 (of thread-time) |
|---|---|---|
| `index.analyse` | 86–90% | 70–78% |
| `index.commit` | 7.6–11.4% | **18.9–25.9%** |
| `index.read` | 2.2–2.8% | 3.1–4.9% |
| `index.enqueue` | ~0% | ~0% |
| `index.enumerate` | 121–130 ms, **5.8% of WALL** | 30 ms, **3.5% of WALL** |

Achieved parallelism is 20.6–21.0x against a ceiling of 23. Three things this settles:

- **Enumeration is not the bottleneck.** The `roots × globs` duplicate walk in
  `PathResolver.EnumerateIndexTargets` costs 121–130 ms on BO3 and 30 ms on CoD4. Removing the
  duplication entirely would save perhaps 85 ms of a 2,100 ms run.
- **`index.commit` is the one worth attention**, and much more so on CoD4 — a fifth to a quarter of
  thread-time against BO3's tenth. That is `BuildRecord` plus `LanguageStore.Upsert`, which takes one
  process-wide write gate and holds it across per-file hashing in three index diffs. A global lock is
  exactly what inflates thread-time at 21x parallelism.
- **`index.enqueue` is free** — but this measurement attaches no cache, so it says nothing about the
  SQLite writer. Measuring that needs a run with `UseCache`.

### Re-measured 2026-08-12, with bo1

Wall-clock, one instrumented run and one not:

| | files | wall-clock |
|---|---:|---|
| bo3 | 1,085 | 1,732 / 1,714 ms |
| cod4 | 904 | 506 / 600 ms |
| bo1 | 2,963 | 1,667 / 1,909 ms |

**All three beat the table above, and bo3 and cod4 do it while instrumented.** cod4 in particular
runs at roughly 60% of its recorded 807–898 ms.

The stage shares moved, in opposite directions:

| stage | bo3 | cod4 | bo1 |
|---|---|---|---|
| `index.analyse` | 89.1% *(was 86–90)* | 73.8% *(was 70–78)* | 65.9% |
| `index.read` | **6.5%** *(was 2.2–2.8)* | **12.6%** *(was 3.1–4.9)* | **18.3%** |
| `index.commit` | 4.4% *(was 7.6–11.4)* | 13.6% *(was 18.9–25.9)* | 15.8% |
| `index.enumerate` | 41 ms, 2.4% of WALL | 29 ms, 5.7% of WALL | 162 ms, 9.7% of WALL |
| achieved parallelism | 21.6x | 20.2x | 19.7x *(ceiling 23x)* |

- **`index.commit` improved and is no longer the headline.** cod4 fell from a fifth-to-a-quarter of
  thread-time to an eighth. It is still 99% `commit.upsert` — 1,334 of 1,392 ms on cod4, 5,099 of
  5,191 on bo1 — so the process-wide write gate is still the shape of it, and bo1's 19.7x is the
  lowest parallelism of the three, which is what a contended global lock looks like.
- **`index.read` is the new one to watch.** Its share roughly tripled on both games and is 18.3% on
  bo1 — 1.4–2.2 ms per file, and this sweep runs fourth, so every file was already in the OS cache.
  Instrumentation inflates `analyse`, which would push read's share *down*, so the figure is
  understated rather than the reverse. Unexplained; measure `File.ReadAllText` and its encoding
  detection before assuming it is I/O. **Answered 2026-08-16, below.**
- **Enumeration is still not the bottleneck**, and bo1 confirms it from the other end: its
  160,382-file raw tree costs 162 ms, which is 9.7% of wall — the largest share of the three and
  still not worth attacking.

The "CoD4 reports 163–231 MB fragmented against 57 MB live" left unexplained above is **resolved**:
cod4 now measures 0 MB fragmented against 58 MB live. `System.GC.ConserveMemory` (below) did it.
What remains is a 441 MB working set on cod4 and 847 MB on bo1 against ~0 fragmentation — pages not
yet returned, which the compaction handles and which this sweep never runs. See the section after
next.

### 2026-08-16: `index.read` was the reader, not the disk

The question above was whether `File.ReadAllText` or the disk owned that share. It was neither the
disk nor the encoding detection: `File.ReadAllText` pulls the file through a 4 KB decode buffer and
grows a builder as it goes, which is what an arbitrary stream needs and not what a script is. A
script is read whole or not at all, so its byte count is known before a character is decoded and one
`GetString` produces the final string. `PhysicalFileSystem.ReadAllText` now reads the bytes and
decodes them once, recognising the byte-order marks longest-first so a UTF-32 mark is not read as
the UTF-16 one it starts with, and replacing invalid bytes rather than throwing — which is what the
framework method did, asserted against it directly in `PhysicalFileSystemTests`.

Instrumented, one run each side:

| | `index.read` before | after | cold index wall-clock |
|---|---:|---:|---|
| bo3 | 1,648 ms (3.7%) | **1,034 ms (2.4%)** | 2,092 → 1,976 ms |
| cod4 | 723 ms (4.7%) | **413 ms (2.9%)** | 747 → 690 ms |
| bo1 | 5,109 ms (11.7%) | **3,483 ms (8.5%)** | 2,219 → 2,062 ms |

Roughly a third off the read stage on all three. Wall-clock moves far less, as it must — read is a
small share of a run that is 78–93% analysis.

Two caveats on this pair, both of which understate rather than flatter it: the baseline run's build
compiled in only the `index.*` scopes while the after run also carried the nested `extract.*` and
`commit.*` ones, so the *after* side paid more instrumentation overhead; and no memory figure is
quoted from either, per the CAUTION above.

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

The steady-state working set is **not** logged at all. `ServerStatusNotifier` samples it every 3 s
and pushes a `gscode/serverStatus` notification when it moves by >= 1 MB, and only AFTER indexing
completes (so it never spams while memory is climbing). The status bar is the readout; there is no
line to grep for. This section described a `Server memory: N MB` log line for some time, and no
such line has ever been written.

The breakdown is gated by the log level rather than an environment variable, so
`gscode.serverLogLevel = verbose` is all it takes. The status-bar tooltip shows the working set
without any log level at all — usually enough, and the reason the log lines can be quiet by
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

**Fix in place:** `Compact` in `Program.cs` runs one `CompactOnce` gen2 collect at
the indexing → serving transition, gated on measured fragmentation (32 MB) so a warm start
skips the pause. The report is logged again afterwards as "Memory after compaction", so any
run shows its own before/after. v1 reached the same conclusion in `cfccd26`, "Force aggressive
GC after workspace indexing completes".

### Measured again (2026-08-05, the LOH attack)

The paragraph above was right about the mechanism and understated the size. Measured per
generation for the first time (`GenerationInfo`, which nothing had ever read), **100% of the
fragmentation is the large-object heap** on every game — gen2 holds none worth reporting:

| | fragmented | gen2 free | LOH free / size |
|---|---:|---:|---:|
| bo1 (2,963 files) | 486.5 MB | 0.0 MB | 486.5 / 542.7 MB |
| cod4 (904) | 189.4 MB | 0.0 MB | 189.4 / 205.8 MB |
| bo3 (1,085) | 104.6 MB | 0.0 MB | 104.6 / 114.4 MB |

Three changes, medians of three cold indexes per game:

| | LOH holes | LOH size | working set | cold index |
|---|---|---|---|---|
| bo1 | 611 → 379 → **0.1** MB | 667 → 435 → **56** MB | 949 → 740 → **581** MB | 2732 → 2336 → **2547** ms |
| cod4 | 199 → 124 → **0.0** MB | 215 → 140 → **16** MB | 377 → 274 → **263** MB | 915 → 824 → **824** ms |
| bo3 | 91 → 77 → **0.0** MB | 101 → 86 → **10** MB | 304 → 338 → **292** MB | 2078 → 2103 → **2063** ms |

Columns are: before, after the token-width work, after `System.GC.ConserveMemory`.

**Retained memory did not move** at any step — bo1 146.0 MB, cod4 51.4, bo3 50.4, identical to
the tenth of a megabyte. That was the constraint, and it is the number to watch: these were
allocation-shape changes, so a live set that moves means something else changed.

1. **`Provenance` is a class** (`PToken.cs`). It describes an expansion *site*, of which a file
   has a handful, but was copied into every token at 48 bytes inside an 80-byte `PToken` — for
   most tokens, three nulls. `PToken` is now 40 bytes and its array crosses the LOH threshold at
   ~2,120 tokens instead of ~1,060. Pinned by `TokenWidthTests`.
2. **Two collections pre-sized** — the preprocessor's output (measured 0.56–0.59 output tokens
   per lexed token; below one because dropping trivia outweighs macro expansion) and
   `SourceText`'s line starts.
3. **`System.GC.ConserveMemory: 5`** in `runtimeconfig.template.json`, in both the server and
   its test project so the probe measures what ships. This is what takes fragmentation to
   *zero*: heap size now equals live. It costs bo1 about 9% on the cold index (2336 → 2547 ms)
   and nothing measurable on cod4 or bo3 — still faster than before the work started.

Reconfirmed 2026-08-12: bo1 0.1 MB fragmented against 147.0 MB live, holding flat across all
sixteen samples; cod4 and bo3 the same at 0.1–0.2 MB. The result stands.

**CAUTION: never take a memory number from a `-p:GscodeInstrumentation=true` build.** The same
probe on the same commit, instrumented, reported bo1 at **79 MB fragmented and 59.9 MB of LOH
holes that never drained** — flat across all sixteen samples, exactly the shape a real regression
would have, and read as one until the uninstrumented run came back at 0.1 MB. The flag changes
allocation shape, not only timing: `PerfTracker` scopes allocate per call on a path that runs
per file. Everything else in this file survives the flag; the memory tables do not.

### The compaction is unconditional, and the fragmentation gate was wrong

`Compact` — then named `CompactIfFragmented` — used to skip the post-index compaction below 32 MB
of measured fragmentation. Once (3) took fragmentation to roughly zero, that gate meant the compaction
**never ran** — and that turned out to cost 446 MB.

Measured on bo1, cache attached, immediately after indexing:

| | working set | fragmented | LOH size |
|---|---:|---:|---:|
| after a forced blocking gen2 | 668.8 MB | 0.8 MB | 56.7 MB |
| after a **compacting** gen2 | **222.5 MB** | 0.0 MB | 56.2 MB |

cod4 shows the same shape, 253.9 → 124.0 MB. Nothing extra was freed — live is 146.2 MB in both
bo1 rows — so this is purely pages being returned to the OS.

**"Nothing is fragmented" and "nothing is being held" are not the same statement.** An ordinary
collection reclaims large-object memory without moving anything or decommitting it;
`LargeObjectHeapCompactionMode.CompactOnce` is the only thing that gives the pages back. Gating
on fragmentation measured the wrong quantity — what a user sees is the working set. It now runs
once per index, unconditionally, on a thread nobody is waiting on.

**Rejected, with evidence** — do not retry these without new information:

- **Pre-sizing the *lexer's* token builder** by the same characters-per-token ratio made bo1
  *worse*, 486 → 596 MB of holes. One ratio cannot fit every file (bo1 spans 2.86 characters per
  token at p10 to 5.87 at p90), so sparse files over-allocated by nearly double and borderline
  arrays that had been *under* the threshold were pushed over it.
- **Turning on the Performance analyzer category** (`dotnet_analyzer_diagnostic.category-Performance`)
  found nothing on a hot path, 2026-08-16: three CA1859 interface-return suggestions on cold paths, one
  CA1827 `Count()`-for-`Any()` on a folder-change notification, and thirty-eight CA1822 "could be
  static" notes. The CA1827 was applied; the setting was not kept, since `TreatWarningsAsErrors`
  would make every future note of that kind a build break for no measured gain.
- **Renting the lexer's buffer from `ArrayPool<Token>.Shared`** fixed the fragmentation but
  **doubled retained memory** (bo1 146 → 279, cod4 51 → 126). The shared pool holds buffers per
  thread and per core across 23 indexing threads and does not release them under a forced gen2.
  It converts holes into live buffers, which is the wrong trade for a long-running server.

Two things this measurement also settled:

- **Do not switch to Server GC.** Per-core heaps would multiply the fragmentation, and
  Workstation GC is the right choice for a language server. The 2026-08-05 `ArrayPool` result
  is the same lesson from another angle: anything that keeps per-core state across the indexing
  threads trades holes for retention.
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

0. For the per-file sweep: set `GSCODE_CORPUS_COD4` and `GSCODE_CORPUS_BO3` — plus
   `GSCODE_CORPUS_BO1` if you want the two facts that name it — and CLEAR the rest (they are
   usually set at the user level and would otherwise be inherited), then
   `dotnet test --filter "Category=Perf"`. Add `-p:GscodeInstrumentation=true` to both the build and
   the test invocation for sub-phases. **Run it twice, once with the flag and once without**, and
   take timing from either but memory only from the uninstrumented run — see the CAUTION under the
   LOH section for what that flag does to a memory reading. Three corpora take about 2.7 minutes.

The remaining steps measure the SERVER, which is a different question — the sweep is sequential and
in-process, while the server indexes cold at `ProcessorCount - 1`:

1. Point `gscode.rawPath` at the game's raw folder (and `gscode.modsPath` at its mods folder, to
   exercise overlay resolution), set `gscode.serverLogLevel = verbose` (the memory lines are
   Verbose) and `gscode.workspaceIndexingMode = full`.
2. Launch the extension against the tools root (see the client `.env` debug flow, or install the
   packaged extension).
3. For cold vs warm: delete `%APPDATA%\gscode\cache\*.db`, start once (cold), restart (warm), and
   read the "indexing complete" line each time.
4. For the steady-state working set, hover the status bar once the process settles. That is the only
   readout — it is pushed as a notification, not logged.

## Measured: the type lattice, and why its 112-byte value was left alone

`ScrValue` replaced a 4-byte `ScrType` as the flow typer's environment value. Measured with
`Unsafe.SizeOf`: **`ScrValue` is 112 bytes**, of which `ScrConstant` is 64 and the `Vec3` inside
that is 24. Twenty-eight times the enum it replaced, in dictionaries that are CLONED per if-arm,
per loop body, per switch case group and per dev block.

That looks alarming and is not, which is the point of writing it down rather than acting on it.
`WorkspaceLints_WhereTheTimeGoes` over BO3's 980 files, one test invocation each so the warm-up
state matches:

| | BO3, 980 files | median | P99 |
|---|---:|---:|---:|
| before the type work (`f5896493`) | 2,528 ms | 0.697 ms | 34.7 ms |
| after it, with five more lint rules | 2,811 ms | 0.664 ms | 36.7 ms |

**+11% in total for five additional rules AND the lattice, with the median slightly BETTER.** The
environments are short-lived gen-0 garbage rather than anything retained, so the size shows up as
allocation churn and not as the steady-state footprint the 400 MB budget covers.

**That +11% is the right answer to the wrong question, and it reads as reassurance it cannot
support.** It is a before/after on one game, run alone, in a process the suite has not warmed. The
per-lint breakdown taken 2026-08-12 asks the other question — what share of lint time these rules
now hold — and answers 31% on cod4 and 40% on bo3. Both figures are true. The delta is small
because the rules replaced a sweep that was already walking the same trees; the share is large
because five rules is a lot of rules. Quote the share when deciding whether to add a sixth, and the
delta only when deciding whether the lattice itself was affordable.

The obvious shrink is available if it is ever wanted — the payloads are mutually exclusive, so
`long`, `double`, `bool` and `Vec3` could share one 24-byte union under an explicit layout and take
`ScrConstant` from 64 to about 40. It is not done because explicit layout beside a GC reference is a
known footgun and the measurement says there is nothing to buy. Re-measure before reaching for it.

Note the invocation mode matters more than the change did: the same test reports 1,595 ms inside a
full `Category=Perf` run and 2,811 ms run alone, because the suite warms the process first. Compare
like with like or the noise swamps the signal.

## Results

Measured on the local BO3-tools machine (corpus not committed):

| Scenario | Corpus size | Measured | Budget | Within budget |
|---|---|---|---|---|
| Cold index | 1,105 files | 5.5 s | < 60 s | yes |
| Warm start | 1,105 files | 2.6 s | < 5 s | yes |
| Steady-state memory (cold, before compaction) | 1,105 files | 390.3 MB | < 400 MB | just inside |
| Steady-state memory (warm) | 1,105 files | 212.2 MB | < 400 MB | yes |
| Live managed set (either path) | 1,105 files | ~115 MB | — | — |

From the test harness rather than the server, 2026-08-12 — a different question, as the section on
cold indexing explains, but the only figures that cover bo1:

| Scenario | Corpus size | Measured | Budget | Within budget |
|---|---|---|---|---|
| Cold index, cache attached, after compaction | 904 files (cod4) | 913 ms, 125.8 MB | < 60 s, < 400 MB | yes |
| Cold index, cache attached, after compaction | 2,963 files (bo1) | 2,122 ms, 228.7 MB | < 60 s, < 400 MB | yes |
| Retained live set, 15 s after indexing | 2,963 files (bo1) | 147.0 MB, flat | — | — |
| Dropped cache writes | either | 0 | 0 | yes |
