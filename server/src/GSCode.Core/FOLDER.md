# GSCode.Core

Neutral foundation types. Zero dependencies — no LSP, no I/O, no game-install paths.

## NameTable.cs

- `sealed class NameTable` — the shared string-interning pool (NOT string.Intern, which
  is uncollectable). `NameTable.Shared` is the process-wide instance; tests make private ones.
  - `Intern(span)` — exact-case interning (display names, literal content). Span-based
    lookup, so checking an existing entry allocates nothing.
  - `InternLower(span)` — lowercase-canonical interning: the form every case-insensitive
    lookup key (identifiers, namespaces, paths) uses, killing ignore-case comparers
    downstream. Already-lowercase input skips the copy.

## Paths/PathUtil.cs

- `static class PathUtil` — THE path normalizer; nothing else calls Path.GetFullPath.
  - `NormalizeAbsolute(path)` — full path, no trailing separator, lowercase, interned.
    This is the ScriptDatabase key format.
  - `NormalizeScriptPath(path)` — game-relative form: backslash separators, trimmed,
    lowercase, interned.
  - `IsUnder(path, directory)` — prefix containment with a separator-boundary check
    (`c:\rootother` is not under `c:\root`).

## Symbols/ScriptLanguage.cs

- `enum ScriptLanguage` — Gsc / Csc; picks which structurally-isolated store a file belongs to.

## Symbols/SymbolKey.cs

- `enum SymbolKind` — what a key identifies: Function/Class/Macro/Field plus the four
  literal kinds (StringLiteral/HashString/LocalizedString/AnimReference).
- `readonly record struct SymbolKey(Namespace, Name, Kind)` — the cross-file lookup key.
  Namespace/Name are lowercase-canonical interned strings (macros and string literals keep
  exact case); Namespace is null for builtins, macros, fields, and literals. Language is
  NOT in the key — GSC/CSC isolation is structural (separate stores).

## Symbols/SymbolModels.cs

- The extracted symbol surface of one file, all records fully populated (empty collections
  and sentinels over nullable "not provided" members):
  - `ParameterSymbol(Name, ByRef, DefaultValueText)` — one declared parameter.
  - `AssignmentSymbol(OwnerName, Name, KeyName, Range)` — one tracked local or field write.
  - `FunctionSymbol` — a top-level function or class method: name/keyname/namespace,
    private/autoexec flags, parameters + varargs, name and full ranges, source file,
    ScriptDoc, and the contained assignments.
  - `MemberSymbol` / `ClassSymbol` — a class `var` member; a class with parent, members,
    methods, ctor/dtor flags, and ranges.
  - `NamespaceSpan(Name, KeyName, NameRange, GovernedRange)` — one #namespace region.
  - `enum ReferenceKind` + `readonly record struct ReferenceEntry(Key, Range, Kind)` — one
    classified reference site; no text stored beyond the interned key.

## Symbols/ScrType.cs

- `enum ScrType` — the small abstract value lattice (Unknown, Undefined, Int, Float, Bool,
  String, IString, Vector, Struct, Array, Entity, Function). Coarse by design: the flow
  typer only asserts a concrete type when certain, else Unknown.
- `static class ScrTypes` — lattice helpers: `DisplayName` (lowercase name for hints/hovers),
  `IsKnown` (concrete and hint-worthy — excludes Unknown/Undefined), and `Join` (control-flow
  merge: equal survives, int+float widen to float, any other disagreement collapses to Unknown).

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
  pipeline stage (lexing = 1xxx, preprocessing = 2xxx). Grows phase by phase.

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
