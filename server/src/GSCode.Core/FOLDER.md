# GSCode.Core

Neutral foundation types. Zero dependencies — no LSP, no I/O, no game-install paths.

## Text/Position.cs

- `readonly record struct Position(int Line, int Character)` — zero-based document
  position; `Character` counts UTF-16 code units (the LSP default encoding, so protocol
  mapping is identity). Implements `IComparable<Position>` plus `< > <= >=` operators
  ordering by line then character. `Position.Zero` is the document start.

## Text/TextRange.cs

- `readonly record struct TextRange(Position Start, Position End)` — a half-open span:
  Start inclusive, End EXCLUSIVE, everywhere in the codebase. Named TextRange to stay
  unambiguous next to `System.Range`.
  - `Contains(Position)` — start-inclusive, end-exclusive membership test.
  - `FromCoordinates(startLine, startChar, endLine, endChar)` — convenience factory.
  - `TextRange.Empty` — zero-width range at the document start.

## Text/SourceText.cs

- `sealed class SourceText` — an immutable text snapshot with a precomputed line-start
  index. All offsets are UTF-16 code units.
  - `From(string)` — builds the snapshot, scanning once for `\r\n`, `\n`, and lone `\r`.
  - `GetPosition(int offset)` — offset → Position via binary search (clamps out-of-bounds).
  - `GetOffset(Position)` — Position → offset (clamps out-of-bounds).
  - `GetLineStart(int line)` — offset where a line begins.
  - `Slice(start, length)` — allocation-free span view over the text.
  - `Text` / `Length` / `LineCount` — raw text and dimensions (LineCount is at least 1).

## Diagnostics/Diagnostic.cs

- `sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, GscDiagnosticCode Code, string Message)`
  — one reported problem. `Create(range, severity, code, args)` formats the code's
  message template from `DiagnosticMessages`.

## Diagnostics/DiagnosticSeverity.cs

- `enum DiagnosticSeverity` — Error/Warning/Information/Hint; values match the LSP wire
  encoding so mapping is a cast.

## Diagnostics/GscDiagnosticCode.cs

- `enum GscDiagnosticCode` — one stable code per reportable condition, grouped by
  pipeline stage (lexing = 1xxx). Grows phase by phase.

## Diagnostics/DiagnosticMessages.cs

- `static class DiagnosticMessages` — the single template table (code → message format).
  `Format(code, args)` renders a message; a code without a template cannot ship because
  formatting it would throw in tests.

## GameProfile.cs

- `record GameProfile` — the portability seam: all game-specific knowledge (extensions,
  global object names, bundled data-file names) flows through this profile so a future
  GSC-dialect port is data, not code changes. `GameProfile.BlackOps3` is the T7 profile.

## Instrumentation/PerfTracker.cs

- `static class PerfTracker` — timing-scope aggregator. Every public method is
  `[Conditional("GSCODE_INSTRUMENTATION")]`, so calls vanish entirely in normal builds
  (enable with `dotnet build -p:GscodeInstrumentation=true`).
  - `Begin(string scopeName)` / `End()` — open/close a scope on the current thread
    (thread-local stack; unmatched End is ignored).
  - `Report(Action<string> writeLine)` — per-scope call count, total ms, mean ms.
  - `Reset()` — clears recorded statistics between measurement runs.
