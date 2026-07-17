# GSCode.Core

Neutral foundation types. Zero dependencies — no LSP, no I/O, no game-install paths.

## GameProfile.cs

- `record GameProfile` — the portability seam: all game-specific knowledge flows through
  this profile so a future GSC-dialect port is data, not code changes.
  - `Id` (string) — short identifier for logs/cache metadata (`"t7"`).
  - `DisplayName` (string) — human-readable game name.
  - `ServerScriptExtension` / `ClientScriptExtension` / `HeaderExtension` (string) —
    `.gsc` / `.csc` / `.gsh` including the dot.
  - `GlobalObjectNames` (ImmutableArray&lt;string&gt;) — built-in globals (`self`, `level`,
    `game`, `world`, `anim`, `classes`).
  - `BundledDataFileNames` (ImmutableArray&lt;string&gt;) — file names the Workspace layer
    loads from its `Api/` folder for this game.
  - `static BlackOps3` — the T7 profile; the only game targeted by the rewrite.

## Instrumentation/PerfTracker.cs

- `static class PerfTracker` — timing-scope aggregator. Every public method is
  `[Conditional("GSCODE_INSTRUMENTATION")]`, so calls vanish entirely in normal builds
  (enable with `dotnet build -p:GscodeInstrumentation=true`).
  - `Begin(string scopeName)` — opens a scope on the current thread (thread-local stack,
    so scopes nest and threads never interleave).
  - `End()` — closes the innermost open scope and adds its elapsed ticks to the shared
    per-name statistics (`ConcurrentDictionary`, interlocked adds). Unmatched `End` is
    ignored, never throws.
  - `Report(Action<string> writeLine)` — one line per scope: call count, total ms, mean ms.
  - `Reset()` — clears all recorded statistics between measurement runs.
