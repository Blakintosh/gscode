# GSCode.Core

Neutral foundation types. Zero dependencies — no LSP, no I/O, no game-install paths.

## NameTable.cs

- `sealed class NameTable` — the shared string-interning pool (NOT string.Intern, which
  is uncollectable). `NameTable.Shared` is the process-wide instance; tests make private ones.
  - `Intern(span)` — exact-case interning (display names, literal content). Span-based
    lookup, so checking an existing entry allocates nothing.
  - `InternLower(span)` — lowercase-canonical interning: the form every case-insensitive
    lookup key (identifiers, namespaces, paths) uses, killing ignore-case comparers
    downstream. The already-lowercase fast path checks whether `ToLowerInvariant` would actually
    change each character, not `char.IsUpper` — `IsUpper` misses a TITLECASE letter (Unicode
    category Lt), which is neither upper nor lower by that test yet still lowercases to something
    different.

## Paths/PathUtil.cs

- `static class PathUtil` — THE path normalizer; nothing else calls Path.GetFullPath.
  - `NormalizeAbsolute(path)` — full path, no trailing separator, lowercase, interned.
    This is the ScriptDatabase key format. A ROOT path (`C:\`, or `/` off Windows) is the one
    exception to "no trailing separator" — it IS its own separator, and trimming it produced a
    drive-relative path (`C:`) or, on Linux, an empty string.
  - `WithoutExtension(path)` — the `#using`/`#include` spelling. Unlike the two normalizers it
    leaves case and separators alone: its output is READ by people, in diagnostic messages and in
    the directives a quick fix writes, rather than used as a comparison key.
  - `NormalizeScriptPath(path)` — game-relative form: backslash separators, trimmed,
    lowercase, interned.
  - `IsUnder(path, directory)` — prefix containment with a separator-boundary check
    (`c:\rootother` is not under `c:\root`).

## Symbols/ScriptLanguage.cs

- `enum ScriptLanguage` — Gsc / Csc / Gsh; picks which store a file belongs to. Gsc and Csc are the
  two structurally-isolated worlds; Gsh is the shared header store, and is a live case wherever a
  question has a per-world answer (`GameProfile.ExtensionFor`, `PrecacheAssetTypes.IsAvailableIn`).

## Symbols/SymbolKey.cs

- `enum SymbolKind` — what a key identifies: Function/Class/Macro/Field plus the four
  literal kinds (StringLiteral/HashString/LocalizedString/AnimReference).
- `readonly record struct SymbolKey(Namespace, Name, Kind, OwnerClass)` — the cross-file lookup key.
  Namespace/Name are lowercase-canonical interned strings (macros and string literals keep
  exact case); Namespace is null for builtins, macros, fields, and literals. Language is
  NOT in the key — GSC/CSC isolation is structural (separate stores).
- `OwnerClass` is the class that scopes the name, or null. A class METHOD is a `Function` with a
  non-null OwnerClass and a null Namespace — the class scopes it instead. Deliberately not its own
  `SymbolKind`: every handler gating on `Kind == Function` should see a method as a function, and a
  new kind would have turned each of those gates into a silent omission.
- OwnerClass is set only where no qualifier was written — a method declaration, a bare call inside a
  class body, `[[self]]->m()`. A written `A::b()` keys with OwnerClass null even inside a class,
  because the qualifier is the identity, and because a dialect may declare a namespace and a class
  with the same name and mean the namespace. The enclosing class of such a call is recovered
  positionally from `ClassSymbol.FullRange` — it describes the call site, not the callee.

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
  - `NamespaceSpan(Name, KeyName, NameRange, GovernedRange)` — one #namespace region: a POSITIONAL
    answer ("which namespace is in effect here"), not the set a file declares into — that set is
    `DeclaredNamespaceSet.From`, below, and reading a span as the set is the trap: a file's leading
    span (before any `#namespace`) is named after the file even when nothing is declared there.
  - `static class DeclaredNamespaceSet` — `From(functions, classes)`: the one definition of "which
    namespaces does this file declare into", derived from the declarations themselves (a namespace
    is reachable exactly when something is declared in it) rather than from `NamespaceSpan`, shared
    by the extraction result and the indexed record so the two can never drift.
  - `enum ReferenceKind` + `readonly record struct ReferenceEntry(Key, Range, Kind, FromMacro)` —
    one classified reference site; no text stored beyond the interned key. `Kind` is WHAT the
    reference is and `FromMacro` is WHERE its text came from: two orthogonal facts that shared the
    enum until an expansion overwriting the kind left every `Kind == Call` rule blind to a call a
    macro produced. When `FromMacro` is set, `Range` is the INVOCATION site, not the callee.
    `IsFunctionCall` is the named call to a script function that five cross-file lints open on.
    Three kinds worth knowing by name. `FieldWrite` is the field on the LEFT of an assignment,
    split from `FieldAccess` because a field is DECLARED nowhere: nothing ever emits a `Definition`
    for one, so go-to-definition answered every field with an empty list until its writes could be
    told from its reads. Not folded into `Definition`, because every write is one and the surfaces
    that read a Definition as THE declaration (the CodeLens anchor, the hierarchies' anchoring step,
    `DeclaresKey`) would each have had to learn that a field's is plural. Both kinds carry the same
    key, so find-references, rename and the `FilesReferencing` index are unchanged.
    `MethodCall` is the `[[expr]]->m()` arrow form, split out from
    plain `Call` because the arrow GUARANTEES a class method even though its receiver's class is
    usually unknown statically — without the split, an untyped arrow call is indistinguishable from
    an unresolved builtin or unqualified call, which forced the resolution lint to suppress every
    namespace-less call on a dialect with classes. `ConcatenatedLiteral` is a string literal that is
    an operand of `+` (a message fragment, not a name); still a reference for find-all-references,
    but excluded from literal completion, which measured and rejected two tempting textual
    substitutes (dropping anything with a space loses 54 real notify/endon events; dropping anything
    single-file-only removes 73% of literals and hides exactly the case completion is wanted for).
  - `readonly record struct FieldBinding(Field, Target, Range)` — what a `owner.field = …` write
    PUTS in the field, where `ReferenceKind.FieldWrite` records only where it happened. Two
    right-hand sides name one thing outright and nothing else does: `new Foo()` and a function
    reference (`&foo`, `&ns::foo`, a bare `ns::foo`) — the same two forms FlowTyper records as
    `ScrValue.InstanceClass` and `ScrValue.FunctionTarget`. Recognised SYNTACTICALLY, so the answer
    survives in the index: a callback is bound in one script and invoked in another, and re-typing
    the binding file per request would mean parsing unopened files on a request path. A bare
    UNQUALIFIED name is deliberately not a binding — `level.cb = foo` reads a local.
  - `AssignmentSymbol.IsLoopVariable` — whether this is a loop's own induction variable (a `for`
    counter, a `foreach` key/value). Still a real assignment for typing and completion; just not
    worth an outline entry, where every loop's `i`/`key`/`value` would drown the names that mean
    something.
  - `FunctionSymbol.IsDevOnly` — declared inside a `/# #/` dev block, so it does not exist in a
    release build; a caller outside a dev block is reported (`DevOnlyFunctionCalledFromRelease`).
  - `ClassSymbol.Constructor` / `Destructor` — the ctor/dtor bodies, kept OUT of `Methods`
    deliberately: they are not callable by name, so listing them there would offer them in method
    completion and count them toward the export signature. Carried at all because their bodies have
    assignments and calls like any other, and without a symbol to hang those on, go-to-definition on
    a local declared inside a constructor had nothing to resolve against.

## Symbols/ScrType.cs

- `enum ScrType` — the coarse PROJECTION of a value's type (Unknown, Undefined, Int, Float, Bool,
  String, IString, Vector, Struct, Array, Entity, Function), for callers that want one name to show
  a user. No longer the lattice itself — `ScrValue`, below, is that, with disjoint bits, constant
  values and entity kinds; `ScrValue.ToScrType()` projects down to this at the public boundary a
  hover or hint reads from. A union that is not exactly one type projects to `Unknown` rather than
  guessing, which is the zero-false-positive rule this whole boundary exists to keep.
- `static class ScrTypes` — lattice helpers: `DisplayName` (lowercase name for hints/hovers),
  `IsKnown` (concrete and hint-worthy — excludes Unknown/Undefined), and `Join` (control-flow
  merge: equal survives, int+float widen to float, any other disagreement collapses to Unknown).

## Symbols/ScrValue.cs

The richer lattice underneath `ScrType`, for a future dialect-to-dialect transpiler. A lint may stay
silent on Unknown; a rewriter must emit something for every expression, so it needs unions and it
needs to know WHY a type is unknown.

- `[Flags] enum ScrTypeSet : ulong` — the set of types a value may hold, as DISJOINT bits. The
  reversal of v1.5's `ScrDataTypes`, which encoded coercions structurally (`Int = 1<<1 | Bool`) and
  paid for it with a subset test that matched ints against bool, an `IsExactly` written to undo it,
  and four rules suppressing wrong type names. Coercion is a relation in `ScrValues.IsAssignableTo`.
  `Universe` is an explicit OR of the members, never `~0`.
- `enum ScrImprecision` — why a value is not exact: an untyped parameter, a script function's return,
  a library spelling the lattice cannot express, an array element, a macro expansion, a branch
  disagreement. `None` with a single-bit set is the only state safe to rewrite blind.
- `readonly record struct ScrConstant` / `Vec3` — a folded compile-time value. New here; v1.5 tracked
  only `bool? BooleanValue` and folded nothing.
- `readonly record struct ScrFunctionRef` — which function a pointer holds, namespace and name, in
  the shape symbol keys use so a consumer can query the database without re-parsing a joined string.
- `readonly record struct ScrValue` — types + constant + tri-state truthiness + entity kinds +
  imprecision. `MustBe`/`MayBe` replace v1.5's single `Indeterminate` flag, which could say "do not
  trust this" but not "it is one of exactly two things and one is unsafe" — the question array
  pass-semantics turns on. `Union` never collapses; `ToScrType()` is the projection that keeps every
  existing consumer unchanged. Structural equality with an agreeing order-independent hash, because
  anything in a dataflow fixpoint needs it (v1.5's reference equality made worklists never converge).

  Two fields carry a value's IDENTITY rather than its type, and neither survives the projection:
  `InstanceClass` (the class of a `new Foo()`) and `FunctionTarget` (the function behind a `&foo`).
  Both are dropped by `Union` when two branches disagree, and both take part in equality and the
  hash. `DisplayName()` is the label surface — the class name, `hash` for a `#"str"`, and otherwise
  `ToScrType().DisplayName()`; a caller JUDGING a type still asks `ToScrType`, which is what keeps
  the typing lints comparing exactly what they always compared.

  `EntityKinds` empty means "entity, kind unknown" rather than "not an entity" — `Union` checks each
  side's `Types` (via `MayBe(Entity)`) before touching its kind list, so a side that cannot be an
  entity contributes nothing, and when both sides may be one, either having an unknown kind widens
  the result to unknown rather than narrowing to the other side's specific kinds. `OfEntity`
  deduplicates its kinds (case-insensitively) so the set never carries a repeat, which is what keeps
  the order-independent XOR hash agreeing with equality.

  `Without(removed)` — the `isdefined`-style narrowing primitive; `Restrict(kept)` keeps only the
  given types. Removing bits recomputes EVERYTHING that depended on them rather than carrying it
  over: `Truthiness` is asked again for the narrower set (removing what made it UNCERTAIN can make
  it certain — `Struct|Undefined` narrowed to `Struct` alone is definitely truthy, not still `null`),
  and `EntityKinds`/`InstanceClass`/`FunctionTarget` are cleared once their own type bit
  (Entity/Instance/Function) is gone, since keeping them would answer an identity question about a
  type the value no longer has.
- `static class ScrValues` — `IsAssignableTo`, `IsByReference(type, arraysByReference)` (the dialect
  fork in one predicate), and `Describe` for rendering.

## Symbols/ScrOperators.cs

- `enum ScrBinaryOp` / `ScrUnaryOp` — operators as SEMANTICS, not `TokenKind`: that lives in
  GSCode.Parser, and this table answers a question with no tokens in it.
- `readonly record struct ScrOperatorResult(Value, Diagnosis)` — what an operator application
  produces, split from whether the engine would even accept the operands. `enum
  ScrOperandDiagnosis` — `Fine` (accept, or not known well enough to object), `UnsupportedOperands`,
  `DivisionByZero`. `Fine` is deliberately also the answer for "not known" — this pass never reports
  on uncertainty, only on a shape it can positively rule out.
- `static class ScrOperators.Apply` — one table, one interpreter, replacing v1.5's 536 lines of
  copy-pasted per-operator bodies. Vector rules are decided BEFORE the string-concat fallback (v1.5
  typed `vector + float` as a string) and match on `MustBe`/`MayBe`, never exact set equality (v1.5's
  rules stopped matching the moment a union flowed in) — including the OTHER side of a vector-shape
  check, not just the side already known to be a vector. Folds constants; detects divide-by-zero from
  the constant rather than from truthiness, which is what let `2 - 2` through and fired on `x / ""`.
  An operand not known to be numeric falls back to a union honest about every OTHER type it might
  still turn out to be (string for `+`, vector whenever either side may be one) rather than a bare
  `Number` — which is the one union `ToScrType` widens back to `float`, so the bare form silently
  claimed a known float for a value nothing established was even numeric.

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
  - `Overlaps(TextRange)` — whether two ranges share any span, TOUCHING ENDPOINTS INCLUDED, unlike
    `Contains`. Inclusive because both callers ask about a user's selection — one that ends exactly
    where a directive begins is one the user means to have selected it — and an exclusive test would
    match nothing at all for an empty (bare-caret) selection, which is what a lightbulb request carries.
  - `FromCoordinates(startLine, startChar, endLine, endChar)` — convenience factory.
  - `TextRange.Empty` — zero-width range at the document start.

## Text/SourceText.cs

- `sealed class SourceText` — an immutable text snapshot with a precomputed line-start
  index. All offsets are UTF-16 code units.
  - `From(string)` — builds the snapshot, scanning once for `\r\n`, `\n`, and lone `\r`.
  - `GetPosition(int offset)` — offset → Position via binary search (clamps out-of-bounds).
  - `GetPosition(int offset, ref int lineHint)` — the same conversion, resuming from `lineHint` and
    leaving it on the line found. A caller walking the text FORWARD (the lexer, twice per token
    including trivia) pays only the lines it crossed instead of a binary search per call — O(lines)
    for the whole file rather than O(lookups × log lines). The hint is purely an optimisation: an
    offset behind it falls back to the binary search, so a stale or wrong hint costs time, never a
    wrong answer.
  - `GetOffset(Position)` — Position → offset, clamping a `Character` past its own line's content to
    THAT LINE's end (before its line break), never past it — a huge `Character` from a stale or
    malformed edit range must not run into every line after it, since `DocumentStore` applies
    incremental edits at exactly these offsets.
  - `GetLineStart(int line)` — offset where a line begins.
  - `Slice(start, length)` — allocation-free span view over the text.
  - `Text` / `Length` / `LineCount` — raw text and dimensions (LineCount is at least 1).

## Diagnostics/Diagnostic.cs

- `sealed record Diagnostic(TextRange Range, DiagnosticSeverity Severity, GscDiagnosticCode Code, string Message)`
  — one reported problem. `Create(range, severity, code, args)` formats the code's
  message template from `DiagnosticMessages`.

## Diagnostics/DiagnosticTag.cs

- `enum DiagnosticTag` — editor presentation hints (`Unnecessary`, `Deprecated`), numbered to
  match the LSP wire encoding so mapping stays a cast. `Unnecessary` is what greys a range out,
  and drives both the excluded-`#if` branches and unused `#using` directives.

## Diagnostics/DiagnosticRelation.cs

- `sealed record DiagnosticRelation(FilePath, Range, Message)` — another location that helps
  explain a diagnostic, such as the first of two competing definitions or the site of a private
  declaration. Paths stay plain strings so Core remains LSP-free; the server maps them to URIs.

## Diagnostics/DiagnosticSeverity.cs

- `enum DiagnosticSeverity` — Error/Warning/Information/Hint; values match the LSP wire
  encoding so mapping is a cast.

## Diagnostics/GscDiagnosticCode.cs

- `enum GscDiagnosticCode` — one stable code per reportable condition, grouped by pipeline
  stage: lexing 1xxx, preprocessing 2xxx, parsing 3xxx, per-file semantics 4xxx, cross-file /
  workspace semantics 5xxx (e.g. NamespaceNotImported). Grows phase by phase.

## Diagnostics/DiagnosticMessages.cs

- `static class DiagnosticMessages` — the single template table (code → message format).
  `Format(code, args)` renders a message; a code without a template cannot ship because
  formatting it would throw in tests. Zero arguments unescapes a template's own `{{`/`}}` by hand
  rather than calling `string.Format` — calling it unconditionally throws on a template that has a
  real `{0}` placeholder but was formatted with no arguments.

## Docs/ScriptDocComment.cs

- `sealed record ScriptDocArgument(Name, Description, Optional)` — one documented parameter.
- `sealed record ScriptDocComment` — the structured doc block, in EITHER dialect's spelling: BO3's
  `/@ @/`, or a pre-BO3 `/* … */` fenced by `///ScriptDocBegin`/`///ScriptDocEnd` (see
  `GameProfile.ScriptDocStyle`). Both reduce to the same key-value line format (Name, Summary,
  Module, CallOn, Spmp, Arguments, Examples — all fully populated, never null), so only the wrapper
  differs. `None` is the empty sentinel and `IsNone` the check consumers use instead of
  null-checking. `Parse(docBlockText)` turns a raw doc block into the structured form; the shared
  MarkdownDocRenderer renders it.
  - `HasTripleSlashFence(commentText)` — whether an ordinary block comment's text actually contains
    a triple-slash doc block. The pre-BO3 games have no doc-comment delimiter of their own, so the
    fence is the only thing separating documentation from a comment that happens to sit above a
    function; without this check every such comment would be read as one.

## Docs/ScriptDocTemplate.cs

- `Render(functionName, parameters, hasVarargs, style, indent)` — an EMPTY doc block for a function
  that has none, with everything the signature already states filled in and the rest left as
  `<summary>`/`<description>` placeholders. The inverse of `ScriptDocComment.Parse`: what this
  writes, that reads back, and `GenerateScriptDocTests` pins the round trip per dialect.
- A parameter with a default value renders as `OptionalArg` in `[brackets]`, everything else as
  `MandatoryArg` in `<angles>` — nothing else in a GSC signature distinguishes the two.
- The pre-BO3 form carries the `///ScriptDocBegin`/`///ScriptDocEnd` fence, which is not decoration:
  a doc block there is an ordinary `/* … */` comment, so the fence is the only thing that makes it
  documentation at all (`HasTripleSlashFence`). Every line is quoted in both dialects, which is how
  the shipped scripts write them.

## GameProfile.cs

- `record GameProfile` — the portability seam: all game-specific knowledge (extensions,
  global object names, bundled data-file names) flows through this profile so a future
  GSC-dialect port is data, not code changes. `GameProfile.BlackOps3` is the T7 profile.
  Equality is BY `Id` alone, hand-written rather than the compiler-generated structural form: the
  lazily-built keyword-index cache is a private field the generated equality would otherwise
  compare too, so two `with`-copies of the identical profile compared unequal purely because one
  of them had answered an `IsKeyword` call and the other had not.
- `enum ImportStyle` — `Namespace` (T7's `#using`, calls stay qualified) or `Include` (every earlier
  game: `#include` MERGES the file's functions into the caller's scope, calls are bare or
  path-qualified). Purely LEXICAL — which directive spelling exists — as opposed to
  `ResolvesByNamespace`, below, which is the resolution question the two happen to coincide with
  today.
- `enum EngineFamily` — `InfinityWard` / `Treyarch` / `SledgehammerGames` / `Unknown`; the studio
  lineage a game belongs to, the biggest predictor of its dialect.
- `enum FunctionPointerStyle` — `PathQualified` (pre-BO3/IW: a bare qualified name IS the pointer,
  no parens — `level.f = maps\mp\_utility::foo;`) or `Ampersand` (BO3: `&foo` / `&namespace::foo`;
  a bare `ns::foo` is always a call).
- `enum ScriptDocStyle` — `TripleSlash` (pre-BO3, both families: a doc block fenced by
  `///ScriptDocBegin`/`///ScriptDocEnd` inside an ordinary `/* … */`) or `AtSign` (BO3's `/@ @/`,
  whose `/# #/` is a dev block instead).
- `EngineNameFallbackPrefix` — the game whose builtin NAMES may stand in when this profile ships no
  library of its own (MW2 borrowing CoD4's). Names only; signatures, documentation and arity stay
  this game's or nothing, which `BuiltinApiSet.EngineNamesFor` enforces by returning a set rather
  than a library.
- `HasTrustedEngineNames` — the one predicate for "may a rule say a name is NOT an engine function":
  this game's library is complete, or it ships none and borrows. It exists because that condition
  was once spelled three ways across two assemblies, two of which could disagree.
- `ResolvesByNamespace` — the RESOLUTION question `ImportStyle` is deliberately kept separate from:
  whether a function's identity includes the namespace it is declared in (true only for BO3).
  Derived from `ImportStyle` rather than settable, so there is exactly one fact to keep straight per
  profile; reading the directive spelling to answer this worked only because the two happen to
  coincide today.
- `KeyNamespace(namespaceName)` — the namespace a function is actually KEYED under, which is not
  always the namespace it is declared in: a merge dialect still has a namespace (it defaults to the
  file stem) but keys by bare name, so rebuilding a key from a symbol's declared namespace without
  going through here silently builds a key nothing is stored under.
- `ToolOutputDirectories` — directory names the workspace walk never descends into (`assetconvert`,
  `texture_assets`), because a workspace folder is often the whole game install rather than a
  scripts folder, and those two alone were 174,657 of a Black Ops III install's 295,640 files.
  Deliberately short and tool-specific: skipping a directory a user keeps real scripts in is a worse
  failure than a slow walk.
- `GameProfile.All` — every mainline game CoD4→BO6 in release order (five `Supported`+`Verified`,
  the rest CORE shells). `ByName(name)` looks one up by short name or Id. `EarliestWithKeyword(word)`
  — the earliest SUPPORTED game whose dialect has a word as a keyword, restricted to supported games
  because a CORE carries only `BaseKeywords` and naming it as the origin of e.g. `foreach` would be a
  claim about a dialect nobody has filled in.
- `GameProfile.Active` — the profile in force for the process (BO3 by default). `Select(name)`
  changes it by short name or Id, returning whether the name was recognised; an unknown or
  unsupported name falls back to BO3 rather than throwing, so a stray setting cannot break the
  server, but the return value is how the caller finds out the setting did nothing.

## Profiles/SupportedProfiles.cs

- `partial record GameProfile` — the registry of named profiles. Keeps supported profiles,
  future core identities, keyword dialects, capability flags, and lookup/enumeration helpers
  together so profile promotion changes one central catalog rather than scattered switches.

## Instrumentation/PerfTracker.cs

- `static class PerfTracker` — timing-scope aggregator. Every public method is
  `[Conditional("GSCODE_INSTRUMENTATION")]`, so calls vanish entirely in normal builds
  (enable with `dotnet build -p:GscodeInstrumentation=true`).
  - `Begin(string scopeName)` / `End()` — open/close a scope on the current thread
    (thread-local stack; unmatched End is ignored). Must pair on the SAME thread with nothing
    that can move to another one (an `await`) between them, or the wrong thread's innermost scope
    gets popped; every call site today is a synchronous block, which is what keeps this safe.
  - `Report(Action<string> writeLine)` — per-scope call count, total ms, mean ms.
  - `Snapshot(IDictionary<string, (double, long)>)` — the same statistics as data rather than
    text, which is what the perf report reads to build its own tables (see `PERF.md`).
  - `Reset()` — clears recorded statistics between measurement runs.
