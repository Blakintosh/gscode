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

## Instrumentation

Timing scopes are compiled in only when the `GscodeInstrumentation` property is set, so normal
builds pay nothing (`Core/Instrumentation/PerfTracker.cs`, `[Conditional("GSCODE_INSTRUMENTATION")]`).

```
dotnet build server/GSCode.slnx -c Release -p:GscodeInstrumentation=true
```

`PerfTracker.Report(writeLine)` dumps per-scope call counts and total/mean milliseconds. Scopes
cover the per-file pipeline stages (lex/preprocess/parse/extract), the indexer fan-out, and cache
read/write.

## How to run the corpus pass

1. Set `TA_TOOLS_PATH` to the BO3 mod-tools install (so `share\raw` and `mods\` resolve).
2. Build the instrumented Release server (above).
3. Run the CI-skipped corpus test category (it auto-skips when `TA_TOOLS_PATH` is unset), or launch
   the extension against the tools root and watch the indexing status counter.
4. For cold vs warm: delete `%APPDATA%\gscode\cache\*.db`, start once (cold), restart (warm).
5. Record `PerfTracker` output and peak working set below.

## Results

_To be recorded on the local BO3-tools machine (corpus not committed)._

| Scenario | Corpus size | Measured | Within budget |
|---|---|---|---|
| Cold index | (files) | (s) | |
| Warm start | (files) | (s) | |
| Steady-state memory | — | (MB) | |
