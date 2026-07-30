# GSCode Performance

Budgets, methodology, and how to reproduce the perf pass. Numbers are gathered on a real
`share\raw` corpus (thousands of stock scripts) via a `GscodeInstrumentation` build; the results
table below is filled from a run on the local BO3-tools machine, since the corpus is not committed.

## Budgets (targets)

| Scenario | Target | Notes |
|---|---|---|
| Cold index (no cache) | < 60 s | Full `share\raw` + all mods, bounded parallelism (cores − 1). |
| Warm start (cache hit) | < 5 s | SQLite restore of unchanged files; only changed files re-parse. |
| Steady-state memory | < 400 MB | Records-only retention for closed files; NameTable interning. |
| Keystroke re-analysis | interactive | Debounced ~250 ms, per-document cancellation; a single file lexes+parses in low single-digit ms. |

## Measured: where analysis time goes

`--filter "Category=Perf"` times every script in a game individually and splits each into the four
phases, writing `temp/gscode-perf-<game>.html`. It is opted into rather than carried along by the
diagnostic sweep, since it costs a second pass over every file.

CoD4 is the control throughout: it has no `#insert` and 20 `#define` across 894 files, so its
preprocess figure is the FLOOR — the cost of walking every token to find nothing to do.

| | files | total | median | lex | preprocess | parse | extract |
|---|---:|---:|---:|---:|---:|---:|---:|
| cod4 | 894 | 1,813 ms | 0.26 ms | 24% | 12% | 33% | 31% |
| bo3 | 980 | 1,369 ms | 0.34 ms | 20% | 24% | 31% | 25% |

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
