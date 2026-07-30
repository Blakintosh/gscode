# GSCode

A Visual Studio Code language extension that provides IntelliSense support for Call of Duty's scripting languages — GSC, CSC and GSH.

GSCode helps you to find and fix errors before the compiler has to tell you, streamlining scripting. Additionally, it adds rich IntelliSense into your editor to support the scripting process.

Black Ops III is the verified target. Call of Duty 4, World at War, Modern Warfare 2 and Black Ops are also supported, each with its own dialect: which keywords exist, whether imports merge (`#include`) or name a namespace (`#using`), and which engine data loads. The status bar shows which game is active.

## Requirements

GSCode's language server requires the .NET 10 Runtime, available at [Download .NET 10.0](https://dotnet.microsoft.com/download/dotnet/10.0). **You do not need the SDK.**

## Release Notes

### 2.0.0 (latest)

A complete ground-up rewrite of the language server and extension for speed, low memory use, and accuracy.

- Rebuilt the entire pipeline from scratch: a span-based lexer, a provenance-tracking preprocessor (`#define`/`#insert`/`#if`), a hand-written recursive-descent parser with error recovery, and symbol extraction — none of it ever throws, so a broken file still gets a full outline and diagnostics.
- Added first-class mod-tools support: `share/raw` plus every mod under `mods/` is indexed in isolation, mod folders overlay raw without crossing each other, and a workspace-only mode works with no game install at all.
- Centralised everything in one script database with structurally isolated GSC and CSC worlds and a shared GSH (header) store, backed by a persistent SQLite cache so cold starts restore unchanged files in seconds.
- Full modern LSP suite: live diagnostics, hover, completion, signature help, go-to-definition, find-all-references (including string/hash/localized/anim literals), document highlight, semantic tokens, folding, selection ranges, document/workspace symbols, code lens, rename, call and type hierarchy, inlay hints, document links, formatting, and code actions.
- Type-flow inference powers inferred-type inlay hints and local-variable hovers, seeded with engine object-field types.
- Formatting (whole document, selection, and on-type) is whitespace-only and corruption-proof: it refuses files with syntax errors and re-checks its own output so it can never alter your tokens.
- Code actions cover remove-duplicate-`#using` and add-missing-`#using`, backed by a namespace-usage lint.
- Macros defined in `.gsh` headers are first-class symbols with go-to-definition, references, and hover via token provenance.
- Added support for four earlier games — Call of Duty 4, World at War, Modern Warfare 2 and Black Ops — with each dialect's keywords, import style, function-pointer and ScriptDoc syntax, and bundled engine data driven by one game profile rather than by branching.
- Replaced `TA_TOOLS_PATH` with `gscode.rawPath` and `gscode.modsPath`, both derived from the game install where possible, so a mod or a loose folder of scripts resolves against the game's own scripts.
- Added snippets for the common constructs, with dialect-specific ones (`function`, `class`, `#using`, ScriptDoc) labelled for the games that have them. `#precache` is split by world, so a `.gsc` is offered only server-side asset types and a `.csc` gets the `client_*` family — a header (`.gsh`) sees both, since it is inserted into whichever world includes it.
- Expanded diagnostics: argument counts against builtin and script signatures, macro arity, unreachable code, unassigned and unused variables, duplicate imports, duplicate case labels, assignment used as a condition, inheritance cycles, `...` placement, and a missing semicolon reported at the end of the statement that is missing it.

### 1.5.0

- Added game script indexing so GSCode can discover namespaces and functions across the workspace and shared raw scripts without every file needing to be opened first.
- Added workspace-wide namespace and `namespace::function` completions, including `sys::` API completions and automatic `#using` insertion for functions from unimported scripts.
- Added field completions for dot-access on common globals such as `level`, `world`, and `game`, with fields learned from indexed scripts.
- Added optional persistent workspace caching for faster startup after scripts have already been indexed.
- Improved `#using` quick fixes so they work for more missing namespace/function cases, insert alphabetically, skip duplicates, and avoid suggesting scripts from the wrong VM.
- Improved protected raw-folder warnings with `gscode.rawFileWarningMode`, warning for stock shared-raw scripts by default while staying quiet for custom scripts kept in `share/raw`.
- Fixed GSH and macro invalidation so changes to inserted files, added/removed macros, and macro body edits are picked up after save without restarting VS Code.
- Fixed several diagnostics, navigation, and highlighting edge cases, including string `.size`, no-op `break` statements, boolean-literal hints, namespace scope leakage, dev blocks, switch expressions, usage detection, and type-flow convergence.
- Updated the extension baseline to VS Code 1.85+ with newer client/server dependencies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the above changes.

### 1.4

- Added a `gscode ignore` comment directive that suppresses diagnostics on the following line.
- Added context-aware completion suggestions based on editor location.
- Significant API updates & improvements aimed to reduce false-positive diagnostics. Added typing to most methods.
- Added type checking against function signatures.
- Added quick fix action capability with action for unused usings.
- Various codebase quality improvements, optimisations, and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the above changes ([#54](https://github.com/Blakintosh/gscode/pull/54), [#63](https://github.com/Blakintosh/gscode/pull/63)).

### 1.3

- Add capability for more detailed diagnostics by 'emulating' select functions, such as `LuiNotifyEvent`.
- Significant memory-focused optimisations.
- Various bug fixes and API updates.

### 1.2

- Re-added indexing support.
- Various optimisations and bug fixes.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed many of the above changes ([#51](https://github.com/Blakintosh/gscode/pull/51)).

### 1.1 

- Various type system improvements, including new support for inference on entity fields.
- Added type inference support for built-in functions (via the API).
- Added `vectorscale` analysis.
- Various bug fixes. 

### 1.0

- Adds semantic analysis steps & type inference associated validation.
- Various bug fixes.
- End of beta phase.

### 0.10 beta

- Disabled workspace indexing temporarily due to performance concerns.
- Added reference finding (Go to Reference, Find All References)
- Added workspace indexing of scripts.
- Fixed switch case analysis with braced bodies.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the above changes ([#30](https://github.com/Blakintosh/gscode/pull/30), [#31](https://github.com/Blakintosh/gscode/pull/31)).

### 0.9 beta

- Added Outliner support for classes, functions, and macros.
- Added goto definition support for usings, script functions, and macros.
- Added signature support for script functions & builtins.
- Fixed function & variable names not showing signatures & tooltips due to case-sensitivity.
- Added analyser checks for: unknown namespace, unused using, unused variable, unused parameters, switch checks.

Special thanks go to [iAmThatMichael](https://github.com/iAmThatMichael) who contributed all of the above changes ([#24](https://github.com/Blakintosh/gscode/pull/24)).

Additionally,

- Added comment code region support (`/* region Name */` `/* endregion */` syntax) with folding ranges in the editor ([#22](https://github.com/Blakintosh/gscode/issues/22)).

### 0.2 beta

- Added a non-contextual completion handler to suggest function completions.
- Added a non-contextual handler to provide GSCode API hover documentation on built-in functions.
- Added diagnostic for missing scripts from using.
- Added basic signature analysis for highlighting of class, function, method and parameter definitions.
- Added using highlight with script path hint.
- Various bug fixes.

### 0.1 beta

- Initial public release. Adds GSC & CSC language support, providing syntax highlighting and IntelliSense for preprocessor and syntactic analysis.

## Reporting Issues and Tweaks

As GSCode is an indepedent implementation of a GSC language parser, it may not immediately have feature parity with the GSC compiler. Any instance where it does not catch bugs that the GSC compiler does will be considered a bug. Additionally, we're hoping to catch more bugs than the GSC compiler eventually.

With that in mind, if you encounter any situations where the GSC compiler (Linker) reports a syntax error, but GSCode does not, this constitutes an issue. You can report these issues to the [issue tracker on GitHub](https://github.com/Blakintosh/gscode/issues); please provide the expected error and attach a script that can reproduce the issue. Issues reporting bugs in isolated script cases without attaching a script (snippet) will not be looked into!

## Known Issues

- Macro hoverables only show nested macro expansions if nested macros are not at the start or end of the expansion.

## Licence

GSCode is open-source software licenced under the GNU General Public License v3.0.

```
GSCode - Black Ops III GSC Language Extension
Copyright (C) 2026 Blakintosh

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
```

Please see [LICENSE.md](https://github.com/Blakintosh/gscode/blob/main/LICENSE.md) for details.
