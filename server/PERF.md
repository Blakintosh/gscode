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

- `Workspace indexing complete: N files in X.Xs` — the cold/warm index time (also mirrored to the
  client "GSCode" channel and the status-bar tooltip).
- `Server memory: N MB` — the working set, sampled every 2 s but logged only when it moves by
  >= 1 MB, and only AFTER indexing completes (so it never spams while memory is climbing).

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
