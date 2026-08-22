# Change Log

All notable changes to the GSCode extension are documented in this file.

This project follows [Keep a Changelog](http://keepachangelog.com/).

## 2.0.0 - Unreleased

A complete ground-up rewrite of the language server and VS Code extension.

### Added
- **Support for five games rather than one.** Call of Duty 4, World at War, Modern Warfare 2,
  Black Ops and Black Ops III, selected with `gscode.game`. Each dialect's keywords, import style
  (`#include` merge vs. `#using` namespaces), function-pointer and ScriptDoc syntax, and bundled
  engine data come from one game profile rather than from branching, and each was checked against
  that game's own shipped scripts. Every later game up to Black Ops 6 is nameable as a *core* over
  the shared base dialect, for a contributor with those tools to fill in.
- New analysis pipeline: span-based lexer, provenance-tracking preprocessor, recursive-descent
  parser with error recovery, and symbol extraction — none of which throw on malformed input.
- **Eight diagnostics 1.5 raised, brought back.** A macro name defined twice, or defined in a header
  and again in the script that inserts it, naming which definition wins (`2017`); a macro parameter
  written twice (`2018`); a switch's second `default:` (`5027`); reading the value of a `thread`
  call, which is `undefined` as soon as the thread waits (`5028`); a `const` whose value is not
  known at compile time, and an assignment to one (`5029`, `5030`); division by a literal zero
  (`5031`); and a statement that computes a value and discards it (`5032`). Each was measured over
  all five games' shipped scripts before being given a severity, and all report nothing there except
  `5028`, whose findings are real.
- Mod-tools support: `share/raw` and each `mods/<name>` indexed in isolation with overlay
  resolution; a first-class workspace-only mode for machines with no game install.
- A single script database with structurally isolated GSC/CSC worlds and a shared GSH store,
  backed by a persistent SQLite cache with two-gate versioning for fast cold starts.
- Full LSP suite: diagnostics, hover, completion, signature help, definition, references
  (including string/hash/localized/anim literals), highlight, semantic tokens, folding,
  selection ranges, document/workspace symbols, code lens, rename, call and type hierarchy,
  inlay hints, document links, formatting (whole/range/on-type), and code actions.
- Type-flow inference for inferred-type inlay hints and local-variable hovers, seeded with
  engine object-field types.
- Corruption-proof whitespace-only formatter (refuses syntax errors; re-checks its own output).
- Code actions: remove-duplicate-`#using` and add-missing-`#using`, backed by a namespace-usage
  lint.
- Commands: `gscode.showOutput`, `gscode.restartServer`, `gscode.clearCacheAndReindex`, and
  `gscode.openApiLibrary` (`shift+f1` in gsc/csc/gsh files).
- In-source suppression carried inside comments: `#pragma disable|restore <code>|all|format`.
  A named code is suppressed at whatever severity it carries, errors and syntax errors included —
  wider than the C# pragma the spelling comes from, which is why `warning` is not part of it (though
  the C# form is still accepted). 1.5's `// gscode ignore` still works as a legacy alias, suppressing
  every diagnostic on the line below the comment.
- A settings surface for the above, under `gscode.*`: the game and script roots
  (`game`, `raw.enabled`, `rawPath`, `modsPath`, `rawFileWarningMode`), indexing
  (`workspaceIndexingMode`, `enableWorkspaceCache`, `diagnostics.scope`), the editor features
  (`outline.showAssignments`, `codeLens.enabled`, the `inlayHints.*` and `completion.*` pairs) and
  formatting (`format.padParens`, `format.maxBlankLines`, `format.sortDirectives`,
  `format.alignConsecutive`).

### Changed
- Requires the .NET 10 runtime.
- Dialect-specific snippets moved from the extension to the server. A contributed snippet is
  registered per language id and one id covers five games, so `foreach`, `class`, `new`, the
  function declaration, `#precache`, the import directives and the ScriptDoc forms used to be
  offered while editing games that do not have them. They are now offered only where the selected
  game has the construct; the universal ones still ship with the extension and work before the
  server has started.

### Removed

Named rather than left to be discovered, because a 1.5 user upgrading loses these.

- **The type-derived diagnostics.** 1.5 ran an abstract-interpretation pass over a control-flow
  graph, and thirteen diagnostics that came out of it are still not raised here: the operator and
  conversion checks (`OperatorNotSupportedOnTypes`, `NoImplicitConversionExists`), argument-type
  checking against the builtin library (`ArgumentTypeMismatch` and its `Unverified` twin), the
  member, index, enumeration and vector-component family (`DoesNotContainMember`,
  `CannotUseAsIndexer`, `CannotEnumerateType`, `InvalidVectorComponent`), the engine-field pair
  (`PredefinedFieldTypeMismatch`, `CannotAssignToImmutableEntity`), the function-value pair
  (`StoreFunctionAsPointer`, `ExpectedFunction`), and the type half of `UnreachableCase`.

  Eight others named in an earlier draft of this entry have since been restored, and are listed
  under Added above.

  Worth being exact about what is missing, and about one thing this entry previously got wrong. It
  said most of 1.5's analysis was already switched off, pointing at commented-out analyser and
  operator-data files. Those files are real, but they were an abandoned earlier generation: the pass
  that replaced them ran on every analysis, and 1.5 shipped editor quick fixes for five of these
  codes. Nothing here was switched off. What 2.0 has instead is type inference that feeds hovers,
  inlay hints and four lints; what it does not have is the union lattice with constant tracking and
  entity subtypes that the thirteen above need. Every other diagnostic layer — lexing,
  preprocessing, parsing, extraction, resolution and arity — is at parity or ahead, across five
  dialects rather than one.

- **The headless CLI.** 1.5 shipped a `GSCode.CLI` project. 2.0 does not, though the layering keeps
  it cheap to restore: the LSP dependency is isolated in the server project, so the parser and
  workspace are already a complete engine without it.

## 1.5.0

- Added game script indexing so GSCode can discover namespaces and functions across the workspace
  and shared raw scripts without every file needing to be opened first.
- Added workspace-wide namespace and `namespace::function` completions, including `sys::` API
  completions and automatic `#using` insertion for functions from unimported scripts.
- Added field completions for dot-access on common globals such as `level`, `world`, and `game`,
  with fields learned from indexed scripts.
- Added optional persistent workspace caching for faster startup after scripts have already been
  indexed.
- Improved `#using` quick fixes so they work for more missing namespace/function cases, insert
  alphabetically, skip duplicates, and avoid suggesting scripts from the wrong VM.
- Improved protected raw-folder warnings with `gscode.rawFileWarningMode`, warning for stock
  shared-raw scripts by default while staying quiet for custom scripts kept in `share/raw`.
- Fixed GSH and macro invalidation so changes to inserted files, added/removed macros, and macro
  body edits are picked up after save without restarting VS Code.
- Fixed several diagnostics, navigation, and highlighting edge cases, including string `.size`,
  no-op `break` statements, boolean-literal hints, namespace scope leakage, dev blocks, switch
  expressions, usage detection, and type-flow convergence.
- Updated the extension baseline to VS Code 1.85+ with newer client/server dependencies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes.

## 1.4

- Added a `gscode ignore` comment directive that suppresses diagnostics on the following line.
- Added context-aware completion suggestions based on editor location.
- Significant API updates & improvements aimed to reduce false-positive diagnostics. Added typing to
  most methods.
- Added type checking against function signatures.
- Added quick fix action capability with action for unused usings.
- Various codebase quality improvements, optimisations, and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes ([#54](https://github.com/Blakintosh/gscode/pull/54),
[#63](https://github.com/Blakintosh/gscode/pull/63)).

## 1.3

- Add capability for more detailed diagnostics by 'emulating' select functions, such as
  `LuiNotifyEvent`.
- Significant memory-focused optimisations.
- Various bug fixes and API updates.

## 1.2

- Re-added indexing support.
- Various optimisations and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the
above changes ([#51](https://github.com/Blakintosh/gscode/pull/51)).

## 1.1

- Various type system improvements, including new support for inference on entity fields.
- Added type inference support for built-in functions (via the API).
- Added `vectorscale` analysis.
- Various bug fixes.

## 1.0

- Adds semantic analysis steps & type inference associated validation.
- Various bug fixes.
- End of beta phase.

## 0.10 beta

- Disabled workspace indexing temporarily due to performance concerns.
- Added reference finding (Go to Reference, Find All References).
- Added workspace indexing of scripts.
- Fixed switch case analysis with braced bodies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the
above changes ([#30](https://github.com/Blakintosh/gscode/pull/30),
[#31](https://github.com/Blakintosh/gscode/pull/31)).

## 0.9 beta

- Added Outliner support for classes, functions, and macros.
- Added goto definition support for usings, script functions, and macros.
- Added signature support for script functions & builtins.
- Fixed function & variable names not showing signatures & tooltips due to case-sensitivity.
- Added analyser checks for: unknown namespace, unused using, unused variable, unused parameters,
  switch checks.
- Added comment code region support (`/* region Name */` `/* endregion */` syntax) with folding
  ranges in the editor ([#22](https://github.com/Blakintosh/gscode/issues/22)).

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed most of the
above changes ([#24](https://github.com/Blakintosh/gscode/pull/24)).

## 0.2 beta

- Added a non-contextual completion handler to suggest function completions.
- Added a non-contextual handler to provide GSCode API hover documentation on built-in functions.
- Added diagnostic for missing scripts from using.
- Added basic signature analysis for highlighting of class, function, method and parameter
  definitions.
- Added using highlight with script path hint.
- Various bug fixes.

## 0.1 beta

- Initial public release. Adds GSC & CSC language support, providing syntax highlighting and
  IntelliSense for preprocessor and syntactic analysis.
