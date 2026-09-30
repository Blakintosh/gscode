# GSCode.Server

The LSP host — the only project referencing OmniSharp. Every live feature is served from here:
diagnostics while typing, the hierarchical outline, folding, selection ranges, navigation,
completion, hover, signature help, code lens, rename, the hierarchies, inlay hints and formatting.

## Mapping/LspMapping.cs

- `static class LspMapping` — the ONLY place Core and protocol types meet: structural
  Position/TextRange conversions (both UTF-16 zero-based) and Diagnostic mapping
  (severity cast, numeric code, source "gscode").
- `LocationAt(path, range)` — a Location for a range in a file on disk. Composition rather than
  conversion, kept here because a file path reaches the client as a URI exactly one way.

## Configuration/ServerSettings.cs

- `sealed class ServerSettings` — the parsed gscode.* view. It covers EVERY key
  `client/package.json` contributes and reads nothing else, which is the invariant worth stating
  rather than a list that goes stale: the game and script roots (game, serverLogLevel, raw.enabled,
  rawPath/modsPath overrides, rawFileWarningMode), indexing (workspaceIndexingMode,
  enableWorkspaceCache, diagnostics.scope), the editor features (outline.showAssignments,
  codeLens.enabled, the three inlayHints.* and the completion.* keys) and the four format.* knobs.
  `Apply(JToken)` merges a settings payload (accepting both dotted and nested key forms); missing
  keys keep current values.

## Configuration/ResolverHolder.cs

- `sealed class ResolverHolder` — holds the current PathResolver (starts empty; the real
  one is built at initialize when settings + workspace folders exist). Consumers read
  `Current` at call time, so swaps/rebuilds need no re-wiring.

## Handlers/AnalysisTiming.cs

- The 250 ms keystroke debounce, in one place. `TextSyncHandler` waits on it and warns against it;
  the corpus `LintBudgetTests` gate asserts each lint rule against a percentage of it. Two readers
  is why it is not a private constant any more — a budget written against a hardcoded 250 would keep
  passing if the debounce were shortened, which is when it would need to fail.
  `DependentDiagnosticsRefresher`'s 900 ms fan-out debounce is a different quantity and stays its own.

## Handlers/TextSyncHandler.cs

- Incremental text sync. didOpen → scheduled onto the thread pool (`Task.Run`, no delay — a file
  opens once, so nothing needs coalescing, only getting off the LSP handler thread, where a window's
  worth of restored tabs would run their analyses back to back); didChange → ~250 ms debounced
  re-analysis with per-document cancellation (superseded runs are cancelled, silently); didSave →
  immediate (bypasses debounce); didClose → clears diagnostics. `AnalysisGate` makes both scheduled
  paths single-flight per document: a second trigger while one is still running queues a rerun
  instead of starting a concurrent second analysis. Publishes diagnostics stamped with the WINNING
  analysis's version (`DocumentStore.AnalyzeSnapshot`), not whatever `document.Version` has become
  by publish time — the two can differ when two analyses of one document race. Before publishing, it
  merges the parse diagnostics with the cross-file lints — all of them, via `DocumentLinter` — for
  GSC/CSC docs, and logs a Warning when one analysis takes at or past the debounce, since that is
  the file a slow-typing complaint would be about. `gscode/gameMismatch` (once per session) goes
  through `ConnectionSettleGate` rather than straight to the client.

## Handlers/DocumentLinter.cs

- `DocumentLinter` — the one call site for `WorkspaceLints.Analyze`, holding the four workspace
  singletons that call needs (database, resolver, builtin API, object fields). Stateless; it exists
  so the seven-argument pipeline signature is written once rather than in every caller. Two
  overloads of `Analyze` share one body: `(OpenDocument, ParseResult)` for the live-editing path,
  `(ScriptLanguage, string path, ParseResult)` for `WorkspaceLintSweep`, which re-lints a CLOSED
  file with no `OpenDocument` to read the two facts off.

## Handlers/DiagnosticsPublisher.cs

- `Publish`/`Clear` — push-model publishDiagnostics wrapper, addressed by PATH. `Remember`/`Forget`
  keep each open document's client-side URI spelling and `UriFor` is the one seam that resolves one,
  so a file is never published under two spellings (the normalized path lowercases on Windows).
- `Forget` also records the CLOSE, and a versioned publish for a closed document is dropped. An
  analysis in flight when a document closes still finishes, and its caller's liveness check and its
  publish are two steps — a `didClose` landing between them ran `Clear` and `Forget` first, and the
  late publish put diagnostics back on a closed file under the normalized URI, where nothing takes
  them away again. A close is remembered only until the document is reopened, and a path the
  publisher was never told about still publishes: only a close is evidence, silence is not.
  Push because closed files have no puller; open documents are a migration candidate to LSP 3.17
  `textDocument/diagnostic`.

## Handlers/DocumentSymbolHandler.cs

- The hierarchical outline: explicit `#namespace` directives become containers (the
  file-default span stays flat) → classes (members + methods) → functions →
  deduplicated assignments (behind outline.showAssignments) — plus macros literally
  #defined in the file (insert-provided ones excluded via provenance).

## Handlers/FoldingRangeHandler.cs

- Maps FoldingRegions.Compute onto LSP folding ranges (code/comment/region kinds).

## Handlers/SelectionRangeHandler.cs

- Expand-selection: the AstSearch ancestor chain per position, linked innermost→parent.

## Handlers/ConfigurationHandler.cs

- didChangeConfiguration → ServerSettings.Apply + live Serilog level switch update.
- Two things are re-requested on an ACTUAL change, because clients push their whole configuration
  on any settings edit: a workspace diagnostics republish when diagnostics.scope moves, and
  `workspace/inlayHint/refresh` when `ServerSettings.InlayFamilies` does. Hints are cached by the
  client, so without the refresh a toggled family does nothing until the next keystroke or scroll,
  and a family turned OFF leaves its hints on screen — both of which read as the setting being
  ignored. Fire-and-forget, like the code-lens refresh.

## Handlers/IndexProgressNotifier.cs

- Maps indexer progress onto the gscode/indexingStarted|Progress|Complete notifications
  (concrete record payloads), coalesced to ≤1 per ~40 ms so the status-bar counter
  races without flooding the pipe; the final count always sends.
- The coalescing lives in `ProgressThrottle`, which is interlocked rather than a `Stopwatch` and a
  comparison: `Progressed` is called from inside the indexer's `Parallel.ForEachAsync` on every
  worker thread, a `Stopwatch` is not thread-safe, and check-then-`Restart` is not atomic, so
  several workers would pass the gate together. It also refuses a count LOWER than the last one
  sent, since a parallel walk reports out of order and the status-bar counter would run backwards.
  `SendNothingBefore` is handed `ConnectionSettleGate.Settled` rather than starting its own timer.
  `Failed(reason)` is the other notification that may not be dropped: it sends
  `gscode/indexingFailed` (not a fabricated `indexingComplete`, which would read as success) when
  the startup pass throws instead of finishing, so the status-bar spinner does not run for the rest
  of the session.

## Handlers/WatchedFilesHandler.cs

- didChangeWatchedFiles → applies each create/change/delete via WatchedFileUpdater
  (registers **/*.gsc|csc|gsh watchers). A branch switch's whole batch applies before
  returning.

## Handlers/WorkspaceSymbolHandler.cs

- workspace/symbol — spans BOTH language stores (no asking file), matching functions and
  classes by case-insensitive substring, each result located at its file. Capped at 256.

## Handlers/NavigationSupport.cs

- `NavigationTarget` + `NavigationSupport.Resolve(uri, cancellationToken)` — shared plumbing
  turning a document URI into its live analysis + the language store and context id to query.
- `SymbolQueryContext` + `ResolveForQuery(uri, cancellationToken)` — the same thing WITHOUT a parse,
  answering from the file's record when it is not open. For the hierarchies: expanding an incoming
  call or a supertype names another file, and that file is normally not one the user has open, so
  requiring an open document would answer those with null — which the protocol reads as "there are
  none". A record already states its context id and declared namespaces, so the fallback costs no
  resolver call and no re-parse; nothing a hierarchy asks needs a syntax tree. `FindAllReferences`
  takes either.
- `IsBuiltinCall` + the builtin arm of `FindAllReferences` — a BUILTIN is not reachable under one
  key either, for a different reason than a method: it has no declaration, so extraction keys each
  call site by the scope it was WRITTEN in, and asking under the asking file's own namespace returns
  only the sites sharing it. Decided here, in the one query every reference-shaped feature runs, so
  the list and the CodeLens count cannot disagree. It takes BOTH halves: the engine library (a name
  nothing declares may simply be a typo, and widening a typo across namespaces would group unrelated
  mistakes) and "nothing declares this key" (a script may declare a function sharing an engine name,
  and inside that namespace the call means the script's). The `BuiltinApiSet` is optional on the
  constructor — a fixture with no engine data disables this widening only, which is the correct
  answer for a caller that cannot tell a builtin from a typo.
- Both overloads take a `CancellationToken`, and it is REQUIRED rather than defaulted.
  `ResolveFresh` runs a full lex, preprocess, parse and extract on the request thread, and every
  caller of it is a read path with no debounce in front of it, so without it a request the client
  had already cancelled would be analysed to the end anyway. `Resolve` takes one too so a handler
  cannot silently pick the uncancellable overload when it meant the freshening one.
- `ResolveHit(target, position)` — the one entry point for `SymbolAtPosition.Resolve`, for every
  position-based handler (hover, definition, references, rename, prepare-rename, highlight, both
  hierarchies, and `BuiltinAtHandler`). A hit's fallthrough on failure — `LocalOccurrencesAt`, since
  the reference index knows nothing about a local — stays written out per handler, because what each
  one does with a local differs too much per feature to be worth unifying (a single range, a
  location list honouring `includeDeclaration`, Read/Write highlight kinds, rename edits).
- `ResolveDirectivePath(target, path)` — the file a `#using`/`#include` names, with the extension
  taken from the ASKING document's language. Go-to-definition and ctrl-click ask this same question,
  so a new directive form is taught here once.
- `FindAllReferences` is the single query behind both the CodeLens count and the peek list, so the
  number and the list cannot disagree. It narrows via `DeclaringFile`, which resolves "which file
  does this key mean, FROM THIS DOCUMENT" — a lens on a declaration answers itself, a call answers
  what it reaches. Applies to every dialect: a BO3 namespace is shared between the `mp` and `zm`
  copies of a script, so without it a count merges both game modes' callers. Matches on the whole
  key, namespace included, or a file's own unrelated same-named function would claim the lens.
- When `DeclaringFile` names one file, the query is `DatabaseQueries.FindReferencesReaching` — the
  files that can reach it, not every file mentioning the key — and only an unnarrowed key collects
  everything and scopes afterwards. Same answer (`ReferenceScopeCorpusTests`); on a merge dialect,
  where `main`'s key is every `main`, it took a whole-file CodeLens at 50,000 files from 707 ms to
  3 ms.

## Handlers/HoverHandler.cs

- Markdown hover: script functions and builtins (fallback), classes, macros, and fields —
  rendered via MarkdownDocRenderer over SymbolAtPosition + DatabaseQueries. Field hover is
  enriched with known engine-field types (every entity kind declaring the name, since the
  owner type isn't inferred until FlowTyper), and `.size` gets its KeywordDocs blurb. When the
  cursor is NOT on a classified reference, it renders a documented keyword/directive
  (`TryKeywordDocHover` over `KeywordDocs`: isdefined, notify, `#using`, …), then falls back to
  FlowTyper's `TryGetLocalTypeAt` to show `(local) name: type` for an inferred local variable.
- `_assignmentCache` — one `InferAssignments` walk per document VERSION, keyed by `ParseResult`
  reference exactly as `InlayHintHandler`'s own cache is. Unlike `InferValues`, that walk carries no
  memoisation of its own, so the fresh `FlowTyper` this built per request re-walked every function in
  the file for every hover over a field — and hovering is a mouse-move away. Only the RESULT is
  shared, never the typer: a `FlowTyper` keeps a cursor and a recording table as instance state, so
  two concurrent requests holding one would interfere, and an `ImmutableArray` cannot.
- `DefinitionLink(path, range)` puts a markdown link to the declaration under the signature of a
  function, class or macro — a fenced `scripts\zm\_util.gsc:118` linked to `file:///…#L118,10`, the label
  script-relative and fenced so a path's backslashes and underscores survive markdown, the target a
  `#L<line>,<column>` fragment because that is how an editor is told to move the caret rather than
  just open the file. The path is always the DECLARING one (`ResolvedFunction.DeclaringPath`,
  `ResolvedClass.DeclaringPath`, a macro definition's `SourceFile`): a symbol reached through an
  `#insert` has a header-true range, and pairing it with the including file's path points at
  unrelated text. Builtins and keywords get none — the engine declares them — and neither does a
  field, whose "definition" is every write that agrees rather than one place.

## Handlers/DefinitionHandler.cs

- Go-to-definition: functions/classes/macros via their Definition references across the
  visible context; #using/#insert paths jump to the resolved target file.
- A FIELD is answered from its write references instead (`FieldWrite` AND `FieldUpdate`, since
  this asks where the field is SET and `level.count += 1` sets it), because a field is declared
  nowhere and so has no Definition entry anywhere - F12 on `level.craftable_shield_grab` returned
  an empty list. Its writes are what the question means for a name that comes into existence by
  being assigned to, and there are usually several, which the protocol already allows for.
  `ScopeToIncludes` still applies, so a file that writes the field itself is offered its own write
  first.
- `ScopeToIncludes` narrows the result to what the call actually reaches: a path call names one file
  and pins to it, anything else prefers the file itself plus `DatabaseQueries.LinkedScriptPaths`.
  Every dialect, not just the merging ones — two BO3 scripts sharing a `#namespace` (the `mp` and
  `zm` copies of `_globallogic_utils`) both answered one key, so a ZM caller was offered the MP
  definition too. A PREFERENCE throughout: with nothing in scope the full set comes back, so a
  missing import still lands somewhere useful instead of dead-ending.

## Handlers/ImplementationHandler.cs

- Go-to-implementation for a class method: the methods that OVERRIDE it, found by walking
  `ClassGraph.DirectChildren` down from the class that declares it and keeping every descendant
  declaring the same method key. Descendants, not direct children — an override two levels down is
  still an override — bounded by a visited set and `MaxDepth`, since a class cycle is a state the
  workspace can be in (`ClassCycleLint` reports it) rather than one the walk may assume away.
- Whether the name IS a method is answered by `MethodResolution.ResolveCall`, not by the syntax at
  the cursor: a bare call inside the class writes no receiver.
- A class MEMBER answers the same way: it HAS a declaration, which is go-to-definition's answer,
  but what it IS is still whatever its plain assignments give it.
- A FIELD is declared nowhere and holds whatever its writes give it, so what it IS, is its plain
  assignments: every `level.foo = ...` in the workspace is one answer. PLAIN only - a `FieldUpdate`
  is left out, which is the line between this request and go-to-definition (that one lists every
  write, because it asks where the field is set rather than what it is).
- A write that binds a FUNCTION answers twice: the assignment line and the declaration it names.
  `level.callback = &on_damage` is GSC's other spelling of an override, nothing in the syntax at
  `level thread [[ level.callback ]]()` names the function, and there is no class graph to walk -
  so the declaration is the destination worth having, and the assignment says which write
  installed it. A bound CLASS contributes no second location; its methods are
  go-to-type-definition's question.
- A top-level function still returns null rather than its own declaration: it has no overrides,
  and falling back would duplicate go-to-definition and hide that the request did not apply.

## Handlers/NavigationSupport.cs - inherited members

- `ResolveHit` VERIFIES every member hit against the class graph before letting it stand. Inside a
  class whose ancestors are not all in the file, extraction records every bare name as a member,
  because it cannot tell an inherited one from a local; the graph settles it here, where the whole
  workspace is available. A name no ancestor declares is a local after all, and answering `None`
  sends it down the local path exactly as before.
- That check is the ONLY resolution a member needs. A cursor-time lookup through `MembersOf` would
  reach no input: a class with an unseen ancestor records every bare name, and one whose chain is
  in-file has every ancestor's members already.
- The walk is bounded by the hierarchy - three classes at BO3's deepest - not by the workspace.

## Handlers/FieldTargets.cs

- What the scripts put IN a field, shared by the four requests that each ask a version of that:
  type definition, implementation, and both hierarchies' prepare step. All four declined on a field
  before, and it read as four unrelated omissions - it is one, since a field is declared nowhere and
  there was no symbol to hand any of them.
- Two forms of every entry point: one that runs the reference query, one that takes a set already
  fetched. Go-to-implementation wants the writes AND what they bind, and running the query twice
  paid the indexed lookup, the shadow rule and the include scoping over again - and left the two
  halves reading sets nothing guaranteed were equal.
- `FunctionsOf`/`ClassesOf` return the declarations; `FunctionKeysOf` returns the KEYS, for call
  hierarchy alone. An item carries the key in `Data`, and the key a write named is not the one
  rebuilt from the declaration it resolved to - an unqualified `&foo` keys with a null namespace.
- Reads `ScriptRecord.FieldBindings`, recorded at extraction, so a callback bound in one script
  answers in another without re-parsing that file on the request path. Cost is bounded by the
  field's WRITES, not the workspace: the reference query is indexed by key, and only the files
  holding a write are read.
- Deduplicated by key, not by site: `level.callback = &on_damage` written in four game modes is one
  implementation.

## Handlers/TypeDefinitionHandler.cs

- Go-to-type-definition for a LOCAL: what the variable holds, from `FlowTyper.TryGetValueAt`.
  Two values carry an identity a declaration can be found for — `ScrValue.InstanceClass` (produced
  at one place, a `new Foo()`, and carried forward through the environment) and
  `ScrValue.FunctionTarget` (`&foo`, a bare qualified name, a pointer dereference).
- A FIELD carries the same two identities, answered from `FieldTargets` rather than from the flow
  pass: a field is invisible to `TryGetValueAt` (which resolves through
  `AstSearch.TryFindLocalContext`), and the write that would answer is usually in another file.
  Classes first, functions only if there are none - the same order the local path takes.
- An ENTITY still answers nothing, which looks like an omission and is not: it is an engine type
  described by the object-field data and declared in no script, so there is nowhere to jump to.

## Handlers/ReferencesHandler.cs

- Find-all-references across the visible context (functions/classes/macros/fields and
  string/hash/istring/anim literals), honoring includeDeclaration.
- A cursor the reference index does not know falls through to `Workspace/Database/LocalReferences`,
  which is every LOCAL — the index is keyed by SymbolKey and shared workspace-wide, so locals are
  deliberately absent from it. Same fallthrough `DefinitionHandler` takes. `includeDeclaration`
  drops only the introduction there (the parameter, or the first write), not every write.

## Handlers/DocumentHighlightHandler.cs

- Highlights every occurrence of the symbol under the cursor within the current file (definition
  sites and both field-write kinds as Write, others as Read - a field has no Definition entry, so
  before those kinds existed the `= 1` that set `level.x` highlighted as a read like any use). A
  compound update colours as a write too; the split between the two write kinds is
  go-to-implementation's, not this handler's. Goes through `NavigationSupport.FindReferencesInFile`
  — the same shared query find-references and the CodeLens count use, ASKED for one file rather than
  asked wide and filtered — instead of a raw key comparison over this file's own
  `Extraction.References`. The raw comparison could not canonicalize a method key the way the shared
  query does, which is exactly the kind of drift the shared query exists to prevent; filtering
  afterwards kept that but built every location in the workspace to do it, 406,326 of them in bo3's
  sampled requests at 50,000 files, on every cursor move. The narrowing is a parameter on the one
  query, so the key derivation, the method union, the scoping and the shadow rule stay shared.
- Locals take the same `LocalReferences` fallthrough, with assignments, loop bindings, `waittill`
  outputs and the parameter all highlighted as Write.

## Handlers/DocumentLinkHandler.cs

- Turns resolved #using/#insert paths into ctrl-clickable links to their target files.

## Handlers/SemanticTokensHandler.cs

- Full-document (and delta/range via the base class) semantic highlighting; the legend
  order mirrors `SemanticTokenType`. Merges two producers before pushing in order:
  `SemanticTokenBuilder.Build` (what the reference index knows — functions, classes, macros,
  fields) and `LocalReferences.SemanticTokens` (parameters and locals, from the same per-function
  walk rename and find-references use). The reference classification wins any position both claim.

## Handlers/CompletionHandler.cs

- `ResolveData` carries `builtin` alongside the name and namespace, because a name can be BOTH an
  engine function and a script one and they are separate rows. Documentation is fetched by a second
  request holding only that blob, so a row that did not say which it was would get whichever the
  NAME resolved to — BO3's builtin `SpawnSpectator` row would render
  `globallogic_spawn::spawnSpectator` under a header reading "builtin". Every value there is a
  STRING, a contract the tests pin, since a serializer's casing can otherwise rename a key.
- Maps `CompletionEngine` entries to LSP items (kind, snippet insert text). Registers the
  trigger characters `. : # & % \ / "` so completion re-fires where it matters (the `"` fires
  literal completion inside a string). Passes the completion.literals setting through to the engine.

- `ImportEditFor(entry, result)` turns a `CompletionEntry.ImportPath` into the entry's
  `additionalTextEdits` — one `#using`/`#include` line at `ImportEdits.InsertionPoint`, the same
  helper the code actions write theirs with, so the two cannot spell one directive two ways. The
  list comes back `isIncomplete` whenever anything in it needed an import or is `Narrowed` (a literal
  or field list cut to the typed text): those candidates are matched on the text typed so far and
  capped, so the page is true for that text only and the editor has to re-ask rather than filter it
  client-side. An empty list is incomplete too — no row is left to carry the flag, and an empty
  complete page is cached for the rest of the word. Everything else is scope-derived and complete,
  which is why this is not simply always on.

## Handlers/SignatureHelpHandler.cs

- Maps `SignatureEngine` results to LSP signature help; triggers on `(` and `,` (retrigger `,`).
- Checks its `CancellationToken` on entry, like completion and workspace symbols: retriggering on
  every comma means the client cancels one request per keystroke, and `ResolveFresh` sits behind no
  debounce. On entry only — one signature is built, so there is no loop worth interrupting.

## Handlers/CodeLensHandler.cs

- "N references" lenses above function/class declarations (counts from the reference index,
  gated by codeLens.enabled). Clicking invokes the gscode.showReferences client bridge. An
  `autoexec` function reads "autoexec entry point" when the count is zero: the engine calls it on
  load, so its call sites are in no script and a bare "0 references" reads as dead code.
- Counts are computed in `Handle`, one `FindAllReferences` per declaration in the file — so the cost
  of the whole request is that query times the file's declaration count, and it is why the query
  must never walk the workspace. `ResolveProvider` is false: there is no lazy resolve step.

## Handlers/WorkspaceFoldersHandler.cs

- `sealed class WorkspaceFoldersHandler` — handles `didChangeWorkspaceFolders` so a multi-root
  workspace needs no restart to pick up a folder. Order is load-bearing: the resolver swaps
  first (every later query classifies paths through it), records under removed folders drop
  next, and added folders index last; re-indexing runs only when something was added, and
  unchanged files restore from cache so it costs a warm start. `NextFolderSet` and
  `ShouldDropOnFolderRemoval` are pure statics so the decisions are testable without protocol
  objects — the latter drops ONLY workspace-context records, since raw and mod files stay
  reachable regardless of which folders are open. `BuildConfig` is shared with `Program.cs`, so
  a rebuild cannot drift from what initialize constructed.

## Handlers/PlanRenameHandler.cs

- `PlanRenameParams`/`PlanRenameEdit`/`PlanRenameResponse` + `sealed class PlanRenameHandler` —
  serves the custom `gscode/planRename` request, returning the `#using`/`#insert` edits a script
  rename implies. A custom request rather than the standard `willRenameFiles` handler because
  OmniSharp 0.19.9 models `FileRename` with a single `Uri` — the spec's `oldUri`/`newUri` pair
  is absent, so a server-side handler cannot learn a rename's destination. The client sources
  the event (which has both) and calls this; all path reasoning stays here via
  `Workspace/Resolution/DependencyRewrite`. Yields nothing when the file is unknown or either
  location sits outside every root.

## Handlers/RenameHandler.cs + PrepareRenameHandler.cs

- Rename functions/classes/macros across every reference in the visible context (mods can't
  see each other, so a rename never leaks across them). prepareRename returns the symbol
  range only for renameable kinds — builtins, keywords, and literals get "cannot rename".
- A LOCAL is always the script's to rename but is invisible to `IsRenameable`, which reads the
  reference index. BOTH handlers take the `LocalReferences` fallthrough, so the preview and the
  rename still cannot disagree — which is the whole reason `IsRenameable` is shared. Refused when
  the function already binds the new name: that case does not fail, it merges two variables.
- `IsLegalNewName` checks the name being renamed TO, which nothing did. An identifier kind takes an
  identifier, judged by `GscIdentifier` — the LEXER's own rule, so it cannot drift from what would
  actually parse; the literals the scripts coin are string content and may hold anything that does
  not end the literal early. Refused silently, like the name-collision case: no rename is
  recoverable, half a rename across every file the symbol reaches is not.

## Handlers/CallHierarchyHandler.cs

- prepare → the function at the cursor; incoming → callers (grouped by containing function);
  outgoing → the functions called inside the body. All from the reference index.
- prepare on a callback FIELD anchors on the functions bound to it. A field is not callable; what
  is callable is what was put in it, and `level thread [[ level.callback ]]()` is how a script
  spells the indirect call whose target the hierarchy exists to trace. Only prepare knows about
  fields - an item is a function either way.
- An incoming caller's item is keyed the way its own callers' references are indexed, because
  expanding it asks for references to that key: a method by its owner class, a function through
  `GameProfile.KeyNamespace`. On a merge dialect the declared namespace is the file stem, which no
  call is keyed under, so a caller keyed on it would expand to nothing
  (`CallHierarchyDialectTests`).
- Methods, end to end (`CallHierarchyMethodTests`). An item's `Data` carries the key's owner class
  as well as its namespace and name, since the item round-trips through the client and a method
  coming back as a free function of the same name would match no call. The containing function of a
  call site is `EnclosingFunction.At`, which finds methods; a top-level-only walk names the FILE as
  the caller of anything called from a method. Outgoing takes `MethodCall` (the arrow form) as well
  as `Call`, and resolves a method key through `MethodResolution` rather than the function lookup,
  which knows nothing of classes.
- Incoming and outgoing resolve their item through `ResolveForQuery`, so a caller or callee in a
  file the user does not have open still answers, and they read the item's own `DocumentUri` rather
  than round-tripping it through `new Uri(string)` — which throws `UriFormatException` on input
  `DocumentUri.Parse` accepts, out of a handler that catches nothing.

## Handlers/TypeHierarchyHandler.cs

- prepare → the class at the cursor; supertypes → its parent (single inheritance);
  subtypes → classes whose parent is this class.
- prepare on a FIELD anchors on the class the scripts put in it (`level.scene = new
  cAwarenessScene()`). Only prepare knows about fields; the walk is the ordinary one, because what
  it walks is a class either way.

## Handlers/InlayHintHandler.cs

- Inlay hints, three independently-toggleable families over the visible range: inferred-type
  hints (`: int`, and `: derived_thing` for a class instance) at each FlowTyper
  `InferredAssignment` name-range end (gated by inlayHints.inferredTypes), and parameter-name
  hints (`amount:`) before each call argument (gated by inlayHints.parameterNames), and macro
  parameter-name hints (`__a:`) before the arguments of a `#define` invocation (gated by
  inlayHints.macroParameterNames, which is OFF by default). The FlowTyper it builds is seeded
  with the shared ObjectFields for field-type inference, and is built only when one of the first
  two families is on — the macro pass reads the preprocessor's invocation list and needs no flow
  analysis. ResolveProvider is false, so the resolve handler is a passthrough.

  Parameter names come from five callee forms. A bare name, a `ns::fn` and the merge dialects'
  `maps\_utility::fn` are answered from the SYNTAX, through `UnqualifiedParameterNames` (which asks
  `CallResolution.UnqualifiedFunction` — the include scope on a merge dialect, the declared
  namespaces on BO3 — then falls back to builtins; a method first inside a class body),
  `QualifiedParameterNames` (namespace first, then the qualifier as a class name) and
  `PathQualifiedParameterNames` (`CallResolution.PathQualifiedFunction`, scoped to the file the path
  names). The two indirect forms are answered from the flow pass
  instead, because their callee is a VALUE and the syntax only names a local: `[[ ptr ]]( … )`
  reads the `ScrFunctionRef` the pointer carries, and `[[ obj ]]->method( … )` reads the object's
  `InstanceClass`. Both showed nothing at all before, which on a BO3 script is most of the
  dispatch in some files. That is why the handler runs `FlowTyper.InferValues` once per request
  when parameter hints are on, rather than a position query per call site.

  Macro hints are a separate pass rather than a relaxation of the call pass's macro guard. By the
  time there is a tree the invocation is gone: the author's call was replaced by the body it
  expands to, and every token of that body reports the invocation's own range, so hinting it
  would stamp the whole expansion onto one call site. `PreprocessResult.MacroInvocations` is the
  only record the call site existed, and its range covers the NAME alone — the arguments are
  found by scanning the file text after it, through the same
  `MacroExpansionPreview.ArgumentSpansFollowing` the macro hover reads, so a hint can never name
  an argument the hover splits differently.

  Three rules decide which hints come OUT, and all three exist because the obvious version was
  wrong. Every hint goes through `AddHint`, which drops a label already emitted at that position:
  a macro naming its parameter twice splices the same argument tokens twice, so the tree holds two
  calls at one range, and a nested invocation inside a `#define` body is re-recorded on every
  expansion of the outer macro. The window is tested against the position the LABEL goes at, not
  against the construct that owns it — testing the call's start dropped every label of a
  multi-line argument list whose callee sat above the viewport. And an argument that already spells
  its parameter's name is left unlabelled — a bare identifier, or an object-like macro name, since
  `DELETE_TRIGGER` against a `delete_trigger` parameter is the same repetition in capitals and the
  tree sees only the literal it expanded to (`MacroNamesByPosition` reads the name back from
  `PreprocessResult.MacroInvocations`, root-file invocations only). A field access whose last
  segment happens to agree keeps its label: that can be coincidence.

  `CollectCalls` takes the window and prunes as it descends (a parser range spans everything its
  node contains; a node with an EMPTY range is descended into anyway, since error recovery is
  normal here), and resolution is memoised per request by (enclosing class, qualifier, name). Both
  are per-frame costs: the client sends one request per visible range.

## Handlers/DocumentFormattingHandler.cs

- Whole-document formatting: runs `GscFormatter.FormatMinimalEdits` over the open document and
  returns its per-region edits (one small edit per run of changed lines, so a caret on an
  unchanged line stays put rather than snapping to the end of one giant replacement). Syntax
  errors or an unsafe reflow (see the formatter's corruption guard) yield no edits.

## Handlers/DocumentRangeFormattingHandler.cs

- "Format Selection". GSC formatting is holistic, so this runs the same formatter and returns
  the minimal edit only when the changed region overlaps the requested range — formatting an
  already-clean selection does nothing.

## Handlers/DocumentOnTypeFormattingHandler.cs

- On-type formatting after `}` or `;`. Reuses the whole-document formatter's minimal edit;
  because the formatter refuses files with syntax errors, a half-typed document is left alone
  until it parses again.

## Handlers/ImportEdits.cs

- `PathOf(path)` — a script path in the form an import directive names it: normalized, with the
  server or client script extension stripped and nothing else (an `#insert` names a header in
  full). The one spelling: the set built from a file's OWN directives is compared against the ones
  built from a declaring record's relative path, so two spellings that drift offer an import the
  file already has. Four copies of this were collapsed into one before it moved here.
- `InsertionPoint<TNode>(result, beforeLine)` — where a new import goes: after the last one of its
  kind, else the top of the file. Serves `#using` and `#include` alike; the `#include` version was
  once a verbatim copy with the node type swapped, the same lesson `FindRemovableDuplicates`
  learned when a `#using`-only helper left the four merge games with a lint and no fix. `beforeLine`
  caps which directives count, so moving a misplaced `#using` cannot target a point below itself.
- Shared rather than private to `CodeActionHandler` because a second writer exists: completion,
  which offers a function from an unimported file and inserts the same directive.

## Handlers/CodeActionHandler.cs

- `RequestLints` — the document's lint pass, run at most once per request and only if something asks
  for it. `DiagnosticsForFixes` wants what lands on the request's LINE and
  `AllUnusedImportDiagnostics` wants the unused imports in the WHOLE document; both need the same
  `DocumentLinter.Analyze` over one unchanged document, so it runs once. Lazy, because an AUTOMATIC
  request (the lightbulb poll, on every cursor move) returns before either consumer runs and must
  keep costing nothing. Per request and dropped with it, the same lifetime rule `CallFixContext`
  states for itself.
- `CallFixContext` — the per-REQUEST state both call fixes share: the name→declarations lookup
  (cached even when it finds NOTHING, which is the common case here), the existing `#using` set, the
  included-path list and both insertion points. A request carries every diagnostic overlapping the
  selection, so computed per diagnostic, twenty unresolved calls would mean twenty store scans and
  forty directive walks for identical answers. Built per request and dropped with it, so it cannot
  go stale against an edited buffer.
- `MissingIncludeFixes` answers 5026 with one "Add #include" per file that declares the name. No
  "create it here" offer, unlike the 5013/5014 fixes: there the name matched nothing and a
  declaration was an honest answer; here the function demonstrably exists and a second copy is a bug.
  Preferred only when a single file can supply it, since several same-named functions is the normal
  state of a merge dialect.
- Quick fixes over the open document. `FindRemovableDuplicates(result, selection)` returns the
  import directives — `#using` AND `#include` — whose (case-insensitive) path was already imported
  earlier and whose line overlaps the selection → a "Remove duplicate ..." QuickFix deleting the
  line, bound to the 5018 reported over it. Each directive keeps its own set, mirroring
  `DuplicateImportLint`; no dialect has both forms, so they never meet.
  `FindMissingUsingSites(result, store, contextId, askingPath, selection)` returns the distinct
  script-relative paths (extension stripped, through `ImportEdits.PathOf`) of visible files defining a
  qualified call whose namespace the file doesn't import, each paired with the call site it
  answers (own-namespace calls and already-imported files skipped) →
  an "Add #using ..." QuickFix inserting the directive after the last existing #using (or at the
  file top). This is the natural fix for the NamespaceNotImported lint. Resolve is a passthrough.
- **Every fix carries the diagnostic it answers.** An action with no `diagnostics` is a general
  lightbulb entry: it is never presented as the fix FOR the error, Auto Fix skips it (that runs
  preferred actions only) and Fix All cannot see it. An action produced correctly without it still
  does nothing when asked for. `FindMissingUsingSites` exists to carry the call's range back out so
  the action can be matched to the reported 5000 — both come from the same `ReferenceEntry`, which
  is what makes the match exact rather than positional guesswork.
- `IsPreferred` is set only where one fix is the answer. Several possible imports means the user
  picks; an empty created declaration is never preferred, since it silences the error without the
  function doing anything.
- `UnresolvedCallFixes(uri, result, store, contextId, askingPath, diagnostic)` — the offers for
  5013/5014. Both codes get the same two, because from the fix's side they are one situation: a
  name with nothing behind it. Which code fired says where the lint LOOKED, not what to do.
  - **Create function 'name'** appends a declaration at the end of the file, opened the way the
    dialect declares one. Offered only for a call written BARE: `other::foo()` names where it
    expects the function, and declaring foo here would not put it there.
  - **Add #using X and qualify with 'ns::'** when the name exists in a namespace this file cannot
    reach. Namespace dialects only — under a merge dialect an unqualified call already resolves by
    name across the include graph, so one that reached the diagnostic is not one an import fixes.
  - Both edits hang off the fact that a call reference's range covers the NAME TOKEN alone (see
    `SymbolExtractor.RecordCalleeReference`). Qualifying is therefore an insert at the range start,
    and a wrong qualifier is replaced over the range scanned back from it.

- `AddGenerateScriptDocAction` — "Generate ScriptDoc block" on a function or method whose `Doc` is
  `None`, rendering `ScriptDocTemplate` in the dialect's style with the declaration's own
  indentation and inserting it above the declaration line. A `Refactor`, not a `QuickFix`, and with
  no diagnostic behind it on purpose: an undocumented function is not a fault — the stock scripts
  ship thousands — so there is no rule to bind to and one would be noise on code that works. Both
  declaration lists are walked, since `Extraction.Functions` holds top-level functions only.
  Skips a nameless declaration (half-typed code is the normal state) and one that arrived through an
  `#insert`, whose ranges are true in the header and whose edit would land in the wrong file.

## Formatting/GscFormatter.cs

- `FormatMinimalEdits(ParseResult)` returns the formatting result as per-region `FormatEdit`s —
  one small edit per run of changed lines, unchanged lines left out entirely — which all three
  formatting handlers share. `Format(ParseResult)` returns the full formatted text (or null).
- `static class GscFormatter.Format(ParseResult)` — a whitespace-only formatter. It emits
  every non-trivia token verbatim and only recomputes the surrounding whitespace: Allman
  braces, one statement per line, one indent per brace level (`AppendIndent`: a tab
  or `tabSize` spaces, from the request's `insertSpaces` — the client defaults all three languages
  to tabs, which is what the corpus does), one more for a line continuing an open `(` or `[`,
  dev blocks and `case` labels by setting, padded non-empty parens (`( x )`, `()` stays tight),
  hugging `.`/`::`/`->` and backslash paths, and blank-line runs capped at `MaxBlankLines`.
  Output is written in the document's own line endings. Line breaks are forced structurally
  (Allman) but original breaks are otherwise preserved, which keeps newline-terminated
  directives (`#define`, `#if`) intact; trailing comments stay glued to their line. Two
  safety properties make corruption impossible: it refuses files with lexer (1xxx) or parser
  (3xxx) errors, and it re-lexes its own output and returns null (no edits) unless the
  non-trivia token stream is byte-for-byte identical to the input's. The aligners run after
  that gate and are checked again; a mismatch there drops the alignment, not the format.

## Program.cs

Top-level entry point and LIFECYCLE only — composition and startup live in
`Composition/ServerServices.cs` and `Startup/`, where they can be unit tested. What is here is what
genuinely has to run at the top level: Serilog setup to STDERR (stdout must stay clean for the stdio
transport; the pipe-transport client shows stderr in the "GSCode Server" output channel), argument
parsing, connecting the transport, handing everything else to `ServerServices.Configure`, and
teardown. Returns a non-zero exit code for a bad command line or a transport that never connected,
and silences CommandLineParser's own `HelpWriter` because it writes to STDOUT, which is the stdio
transport's wire.

Waits for exit, cancels and bounded-awaits `IndexingLifetime` BEFORE `CacheHolder.CloseAsync()` (so
an in-flight cache write is not silently dropped by closing out from under it), then disposes the
transport owner and flushes logs.

## Logging/StartupLogController.cs

- `sealed class StartupLogController` — the level switch's startup-floor state, shared between
  `ServerServices.OnInitializeAsync` (which learns the requested level and calls `SetRequested`,
  applying `ServerLogLevel.StartupFloor` immediately) and `StartupIndexRunner` (which calls
  `Settle()` once startup is over — at the end of the indexing task, or at the end of `OnStarted`
  when indexing is off). Exists so those two files, on opposite sides of the `Program.cs` split,
  can agree on the level without a top-level local either of them could close over. The startup
  lines are written at Information and the client's default is `warning`, so settling to the
  requested level any earlier discarded every one of them: the roots, the effective settings, the
  index breakdown and how long the server took to be ready.

## Composition/ServerServices.cs

- `internal static class ServerServices` — `Configure(options, …)` registers every singleton the
  handlers resolve, then the full `AddHandler<T>()` chain, then wires `OnInitialize`,
  `OnInitialized` and `OnStarted`. `OnStarted` itself is one line handing off to
  `StartupIndexRunner.RunAsync` — see `Startup/StartupIndex.cs` — because it needs the cache and
  indexing machinery this class has no other reason to touch.
- `OnInitializeAsync` applies the client's settings, selects the game profile as a safety net for a
  host that did not pass `--game`, builds the `PathResolver` from the resolved workspace folders,
  and logs the roots and the effective settings. `OnInitializedAsync` declares UTF-16 position
  encoding explicitly — every range this server produces comes from `SourceText`, which indexes
  UTF-16 code units, so a client negotiating UTF-8 offsets would silently mis-place ranges in any
  file containing astral characters.
- `RootSource`, `LogEffectiveSettings` and the three `Load*` data-file factories
  (`LoadBuiltinApi`/`LoadObjectFields`/`LoadStockScripts`, each wrapped by `LogDataFile`) are
  private here rather than in `Startup/IndexReporting.cs`: they are read once, by the registrations
  and the initialize hook right beside them, and nothing outside this file calls them.

## Startup/StartupIndex.cs

- `internal sealed class StartupIndexRunner` — the whole `OnStarted` body: sends `gscode/serverReady`
  through `ConnectionSettleGate`, opens the persistent cache (regardless of `workspaceIndexingMode`,
  so `gscode/clearCache` and `ServerStatusNotifier` both have something to act on even with indexing
  off), and — when the mode is not `off` — launches the startup index as a `Task` held by
  `IndexingLifetime` rather than fire-and-forget, cancellable by shutdown and by `gscode/clearCache`.
  When the mode is `full`, `WorkspaceLintSweep.RunFullSweepAsync` runs after the index and before
  `WorkspaceDiagnosticsPublisher.Refresh()`, so a closed file's Problems entry is upgraded before
  anything republishes it. On indexing completion it logs `Workspace indexing complete: N files in
  X.Xs` (info), a `full`-mode sweep its own `Workspace lint sweep complete` line, then
  `IndexReporting.LogIndexBreakdown`, and then starts `ServerStatusNotifier.RunAsync`. A failure
  anywhere in the startup task sends `gscode/indexingFailed` instead of leaving the client's
  progress UI spinning forever; either way, the `finally` block calls
  `StartupLogController.Settle()` — startup is over however it ended.
- Constructed with an `IServiceProvider` and an `ILanguageServerFacade` rather than whatever type
  OmniSharp's `OnStarted` hands the caller — both are ordinary types already used elsewhere in this
  project (every handler resolves the facade the same way), so `RunAsync` depends on nothing this
  call site would need to learn a new name for.
- `Compact()` — the one compacting collection at the indexing-to-serving transition. Lives here
  rather than in `IndexReporting.cs` because it is a GC operation the startup task performs, not a
  log line it prints; `IndexReporting.LogMemoryReport`, called before and after it, is what reports
  the effect.

## Startup/IndexReporting.cs

- `internal static class IndexReporting` — startup-time logging helpers with no state of their own:
  `LogIndexBreakdown` (per-language file counts split by raw/mod/workspace context, via
  `CategorizeContext` + `FormatLanguageLine`, plus a totals line of functions · classes · macros ·
  distinct namespaces), `LogMemoryReport` (the managed-heap-vs-working-set breakdown, via
  `AppendGenerations` for the per-generation fragmentation), `BundledDataFilePaths` (feeds the
  cache's build-identity hash), and `ServerVersion` (read from the assembly so it cannot drift from
  what actually shipped). `ServerVersion` is `internal` because `Program.cs` itself calls it twice,
  at the top and bottom of the process; the rest are called only from `StartupIndexRunner`.

## Transport/TransportOptions.cs

- `class TransportOptions` — CommandLineParser options: `--pipe <name>` (VSCode default),
  `--socket <port>`, `--stdio` (also the fallback when nothing is given), `--game <short name>`.

## Transport/TransportResolver.cs

- `static class TransportResolver`
  - `record ResolvedTransport(Stream Input, Stream Output, IDisposable? Owner, string Description)`
    — the connected streams; `Owner` (pipe/tcp client) must be disposed on shutdown.
    `Description` is what `Program.cs` logs, since which transport is in use is the first thing a
    "the server never started" report needs.
  - `ResolveAsync(TransportOptions, CancellationToken)` — connects the selected
    transport. Strips the Windows `\\.\pipe\` prefix VSCode puts on pipe names before
    handing the bare name to `NamedPipeClientStream`.
  - Transports are COUNTED rather than tested in precedence order, so naming two
    (`--stdio --pipe x`) is refused instead of silently using one of them, and `--stdio` is read
    rather than working only by falling through. An empty `--pipe` is refused here rather than
    inside the BCL.
  - Both connects are bounded (30 s). The client creates its pipe and then spawns us, so a wait
    that reaches the bound means the other end is gone; unbounded, a client that died between
    spawn and listen left an orphaned server waiting forever.

## Logging/ServerLogLevel.cs

- `static class ServerLogLevel`
  - `FromSetting(string?)` — maps the client's `gscode.serverLogLevel` string
    (off/error/warning/info/verbose) to a Serilog level; `off` maps to a level past
    Fatal so the channel is truly silent; unknown values fall back to info.

## Configuration/CacheHolder.cs

- `CacheHolder` — owns the persistent cache's lifetime so handlers can reach it through DI. The
  cache opens during startup, after settings and workspace folders have arrived, which is too late
  for constructor injection — hence a holder, matching `ResolverHolder`.

## Configuration/IndexingLifetime.cs

- `IndexingLifetime` — owns the startup indexing task's `CancellationTokenSource` and the task
  itself, so exit and `gscode/clearCache` can both `CancelAndWaitAsync` (bounded) it before
  `CacheHolder.CloseAsync()` runs. Run detached on `CancellationToken.None`, a server closed
  mid-index would race its own cache close: in-flight `SqliteCache.Enqueue` calls would land on an
  already-completing write channel and be silently counted as dropped. One token covers the whole
  startup task — the index, the full-mode lint sweep, the settle delay, the cache drain and
  `ServerStatusNotifier` — so a single cancellation reaches all of it.

## Configuration/ConnectionSettleGate.cs

- `ConnectionSettleGate` — the shared "has the pipe had a moment to settle" clock
  (`Task.Delay(500)`, started lazily on first read). A notification sent inside the
  initialize/initialized window is silently dropped by the transport; shared, because
  `gscode/serverReady` and a restored tab's `gscode/gameMismatch` need the protection as much as the
  indexing notifications do. `SendOnceSettled(Action)` runs a send once settled (or immediately, if
  already settled) via a continuation rather than an awaited `Task`, so a synchronous LSP handler
  can use it without becoming async. `RunOnceSettled(Task, Action)` is the same deferral against a
  clock the caller already holds, which is how `IndexProgressNotifier` defers its two terminal sends
  — one shape rather than four copies. Not `ExecuteSynchronously`: `Task.Delay` completes on a TIMER
  thread, and running the send inline there would serialise a notification and write it to the pipe
  ahead of every other timer in the process. A failed send is logged rather than becoming an
  unobserved task exception.

## Formatting/

`FORMATTING.md` is the behaviour spec — every rule, and the measurements over the shipped scripts
that chose it. These are the pieces that implement it:

- `FormatOptions` — the knobs the formatter honours: the editor's indentation settings, which arrive
  per request in the LSP payload, plus the GSC-specific ones from configuration. The defaults are a
  fallback for callers with no editor to ask.
- `FormatScope` — which lines an on-type format may touch: the alignment GROUP around the cursor
  rather than the whole block, so a keystroke tidies what you are working on and stops there.
- `AssignmentAligner` — lines up the operators in a run of assignments at one indentation level, one
  space past the longest left-hand side.
- `ColumnAligner` — the same idea for the INTERIOR of subscripts and call arguments: a run of
  statements sharing a shape has each bracket and argument column padded to its widest.
- `DirectiveSorter` — groups and sorts the directive block at the top of a file. The formatter's one
  operation that MOVES code rather than whitespace, so it runs as a post-pass on already-reflowed
  text, after the token-stream equality gate. `#using` and `#include` share a group (one idea, one
  spelling per dialect) and sort with `#precache`; `#insert` and `#define` keep their order, and a
  `#define` above an `#insert` stands the whole pass down. `#using_animtree` ENDS the block: it
  binds every `%anim` below it until the next one, so it is not a preamble directive at all and
  moving it rebinds animations invisibly. A comment run travels with the directive beneath it,
  except the run above the FIRST directive — that is the block's banner and stays above it, with
  whatever spacing the author left — and a run followed by a blank line part-way down, which is
  owned by nothing and so ends the block.
- `LineFacts` — shared line-level premises: comment tokens, leading whitespace, code-only tokens,
  comment-only lines, `BucketByLine` (a line's significant tokens, whitespace and newlines
  dropped), `TopLevelAssignment` (the one bracket-depth walk that decides a line is an
  assignment) and `GatherRun` (a run of member lines with comments stepped over). Keeping these in one place prevents the aligners and formatter scope logic from
  disagreeing.
- `FormattingSupport` — the steps the three formatting handlers share before they diverge. An
  injected singleton, like `NavigationSupport`, owning the document store, resolver, stock-script
  list and settings those steps need, so each handler takes only it and its selector:
  `OptionsFor` builds the formatter options from the editor's indentation and the settings,
  `Prepare` resolves the open document and analyses it FRESH before diffing (a stale read here
  writes a corrupting edit rather than merely showing something wrong), `ToLspEdits` projects the
  formatter's per-region edits onto the protocol. Each handler then keeps whichever edits its
  feature is scoped to. With `fixCasing` on it also resolves the document through
  `NavigationSupport` and hands the formatter a `CallCasing` lookup.
- `GscFormatter.Casing.cs` — the `fixCasing` pass: which tokens are keywords, functions (bare,
  qualified, threaded, referenced), namespace or class qualifiers, and classes after `new` or as a
  base; and the macro guard (macro names match exactly, so a macro use is never recased and no fix
  may spell one; a function-like macro is only a use before a `(`). Its fixes are the only
  difference the token gate permits.
- `ICasingLookup` — the spellings the pass asks for, answered by something that can see the
  workspace, which the formatter cannot.
- `CallCasing` — the workspace's `ICasingLookup`. A bare call takes a builtin's spelling before a
  script function's, since it resolves to the builtin first; a qualified or threaded call, or a
  reference, takes the script function's. Namespaces come from `NamespaceSpan.Name` (the
  `#namespace` directive's spelling), classes from their declarations.

## Handlers/ — the remainder

- `BuiltinAtHandler` — serves the `gscode/builtinAt` request behind `shift+f1`, since the client has
  no symbol knowledge of its own and cannot tell a builtin from a script function.
- `CodeLensRefresh` — `ICodeLensRefreshSink`, the one thing the dependent refresher asks of the
  connection (tell the client its lenses are stale), plus the real and null sinks. A seam for the same
  reason `IDiagnosticsSink` is one: a test that only checks whether a refresh was asked for should not
  implement `ILanguageServerFacade`. Also `ClientRefresh.Request(server, method)`, the one
  fire-and-forget send behind every `workspace/*/refresh` request — the code-lens sink's and
  `ConfigurationHandler`'s inlay-hint one — so a failure is observed the same way for each.
- `ClearCacheHandler` — cancels the startup indexing task (`IndexingLifetime.CancelAndWaitAsync`,
  so it stops enqueueing into a cache about to close) THEN drains the cache and deletes only THIS
  workspace's database, server-side where the paths are known.
- `DependentDiagnosticsRefresher` — debounced re-linting when the world under a file moves. Two
  halves. OPEN documents: every other open tab, reusing its cached parse instead of reparsing. Three
  callers, and the second and third pass no origin because the event belongs to no open document: an
  edit that changes a file's exported cross-file signature (`TextSyncHandler`), a change arriving on
  disk behind the editor's back (`WatchedFilesHandler`), and the completion of the initial index
  (`Startup/StartupIndex.cs`). The last is not optional — a tab restored with the window is opened
  during initialize, so its `didOpen` linted it against a half-built index, and the codes gated on
  `HasCompletedIndex` (5013/5014/5025/5026) would stay silent until it was closed and reopened.
  CLOSED files, `workspaceIndexingMode: full` only, gated on `ScriptDatabase.HasCompletedLintSweep`:
  `ClosedDependentsOf` names the files `LanguageStore.FilesReferencing` says mention a function the
  ORIGIN declares, and `WorkspaceLintSweep.RelintClosedFilesAsync` re-lints just those — a rename
  costs the files that mention the name, not the workspace. Only fires for a caller-named origin
  (not the on-disk-change case) and only covers top-level function declarations, not classes or
  methods — stated gaps. Origins a pass does not finish with are handed BACK when it is cancelled:
  the set is cleared the moment a pass takes it, so cancellation after that point would drop them,
  and nothing else re-lints a closed dependent. Cancellation only — returning them after a FAILURE
  would reschedule the same failing pass every 900 ms for the rest of the session.
- `PrepareRenameHandler` — validates a rename before the UI opens: the symbol's range for anything
  the SCRIPTS define, null for what the ENGINE defines (builtins, engine fields) and for keywords, so
  the editor says "cannot rename here" instead of prompting and then failing. Shares
  `RenameHandler.IsRenameable`, so the preview and the rename cannot disagree.
- `SingleFlightAnalysis` — one analysis in flight per document: runs now if nothing is, otherwise
  queues a rerun for the running call to pick up. Pulled out of `TextSyncHandler` with no dependency
  beyond `DocumentStore`, so the races against a document being CLOSED or REPLACED by a second
  `didOpen` mid-analysis can be tested directly. The liveness check sits INSIDE the loop, guarding
  only the analysis: a rerun queued before the loop's first check is still serviced.
- `SupportedGamesHandler` + `GameRoster` — the games a picker may offer, in release order, and the
  one in force. One list with two callers (the picker command, and `gscode/gameMismatch`'s payload),
  because only the server knows which profiles are `Supported` — a client-side copy drifts, and one
  did to nine games, four of them cores whose selection the server resolved back to Black Ops III.
- `ServerStatusNotifier` — keeps the status-bar tooltip's memory figure current. The megabyte
  divisor is its own constant rather than the reporting threshold reused: the threshold is a tuning
  knob and the divisor is a unit, and as one constant, raising the threshold would silently change
  what the number MEANS. Starts immediately when `workspaceIndexingMode: off` (nothing to wait for),
  or after indexing finishes otherwise, on `IndexingLifetime.Token` rather than
  `CancellationToken.None`, so shutdown stops it.
- `WorkspaceLintSweep` — the `full` mode itself: runs the cross-file lints over every indexed
  GSC/CSC record (`RunFullSweepAsync`, once after the startup index) or a named subset
  (`RelintClosedFilesAsync`, from `DependentDiagnosticsRefresher`), storing the merged result via
  `ScriptDatabase.SetDiagnostics` and dropping the re-parsed `ParseResult` — records still retain
  none. Skips any path that is open (the live-analysis path already covers it, from text that may
  be ahead of disk); `SetDiagnostics`'s content-hash gate skips a path that changed underneath the
  sweep, in either direction, rather than writing diagnostics that no longer describe the record.
- `WorkspaceDiagnosticsPublisher` — publishes problems for files that are not open, per
  `gscode.diagnostics.scope`. It skips open documents deliberately, since `TextSyncHandler` owns
  those and publishes a richer set for them — which is why every caller of its `Refresh()` has to
  pair it with `DependentDiagnosticsRefresher.Schedule()` to cover the other half.
- `Refresh()` sends a file only when its diagnostics differ from what was last sent — it remembers
  the array per path and compares by reference, since a record's diagnostics are replaced wholesale
  whenever they are recomputed (erring toward resending, never toward staleness). It runs after
  every re-lint of an edit's closed dependents, where resending every file with a problem would
  flood the client. Files no longer reported are still taken back.

## .editorconfig

Project-local override disabling CA2007 (ConfigureAwait): OmniSharp hosts no
SynchronizationContext, so handler code stays uncluttered per the house async rules.
