# GSCode v2 — Performance

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

## What the server logs (no special build needed)

At `gscode.serverLogLevel = info`, the "GSCode Server" output channel logs the two headline
numbers directly:

- `Workspace indexing complete: N files in X.Xs (M from cache)` — the cold/warm index time (also
  mirrored to the client "GSCode" channel and the status-bar tooltip). The cache count makes a
  run self-identifying as cold or warm.
- `Memory after indexing:` — a one-shot breakdown at the indexing → serving transition (below).
- `Server memory: N MB` — the working set, sampled every 2 s but logged only when it moves by
  >= 1 MB, and only AFTER indexing completes (so it never spams while memory is climbing).

### Reading the memory breakdown

```
Memory after indexing:
    files              1,105  (0 restored · 1,105 analysed)
    working set        400.0 MB   (what the OS reports)
    managed live       180.0 MB   (retained objects)
    heap size          350.0 MB
    committed          390.0 MB
    fragmented          40.0 MB   (mostly large-object heap)
    collections     gen0 120 · gen1 40 · gen2 6
```

**The number that matters is the gap between "managed live" and "working set."**

Cold indexing allocates heavily per file — source text, token arrays, the PToken stream, the
AST, extraction builders — at `ProcessorCount - 1` way parallelism, and all of it is garbage
once the `ScriptRecord` is built. A warm start only deserializes records from SQLite, so it
never allocates at that scale.

The open question this exists to settle (observed 2026-07-20: ~400 MB cold vs ~200 MB warm on
1,105 files):

- If **managed live is similar** across a cold and a warm start while the working set differs,
  the extra footprint is grown, uncompacted heap rather than retained data. A high
  `fragmented` figure points at the large-object heap specifically, which is not compacted by
  default. The fix is a one-time compacting collect at this transition
  (`GCLargeObjectHeapCompactionMode.CompactOnce` plus a blocking gen2 collect) — v1 reached
  the same conclusion in `cfccd26`, "Force aggressive GC after workspace indexing completes".
- If **managed live is genuinely higher** after a cold start, something in the analysis path is
  being retained that should not be, and the answer is a leak hunt, not a GC call.

Do not switch to Server GC to chase this: per-core heaps would make the footprint worse, and
Workstation GC is the right choice for a language server.

Worth checking while measuring: whether the cache-restore path interns strings through
`NameTable`. If it does not, a warm start looks leaner but carries duplicate strings that
interning exists to eliminate — making the warm number flattering rather than genuinely better.

## Deeper timing (optional instrumentation)

Per-stage timing scopes are compiled in only when the `GscodeInstrumentation` property is set, so
normal builds pay nothing (`Core/Instrumentation/PerfTracker.cs`,
`[Conditional("GSCODE_INSTRUMENTATION")]`):

```
dotnet build server/GSCode.slnx -c Release -p:GscodeInstrumentation=true
```

The indexer wraps its fan-out in an `index.total` scope. NOTE: a `PerfTracker.Report(...)` dump
is not yet surfaced automatically — the two logged numbers above are the current perf signal;
wiring a report dump + a `TA_TOOLS_PATH` corpus test is a tracked follow-up.

## How to run the pass

1. Set `TA_TOOLS_PATH` to the BO3 mod-tools install (so `share\raw` and `mods\` resolve), set
   `gscode.serverLogLevel = info` and `gscode.workspaceIndexingMode = full`.
2. Launch the extension against the tools root (see the client `.env` debug flow, or install the
   packaged extension).
3. For cold vs warm: delete `%APPDATA%\gscode\cache\*.db`, start once (cold), restart (warm), and
   read the "indexing complete" line each time.
4. Read the steady-state "Server memory" line once the process settles.

## Results

Measured on the local BO3-tools machine (corpus not committed):

| Scenario | Corpus size | Measured | Budget | Within budget |
|---|---|---|---|---|
| Cold index | 1,105 files | 5.5 s | < 60 s | yes |
| Warm start | 1,105 files | 2.6 s | < 5 s | yes |
| Steady-state memory | — | (read "Server memory" line) | < 400 MB | |
