# Change Log

All notable changes to the GSCode extension are documented in this file.

This project follows [Keep a Changelog](http://keepachangelog.com/).

## [2.0.0] - Unreleased

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
