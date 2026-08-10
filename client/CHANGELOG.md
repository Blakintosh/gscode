# Change Log

All notable changes to the GSCode extension are documented in this file.

This project follows [Keep a Changelog](http://keepachangelog.com/).

## [2.0.0]

A complete ground-up rewrite of the language server and VS Code extension.

### Added
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
- In-source suppression carried inside comments: `#pragma warning disable|restore <code>|all|format`.
  1.5's `// gscode ignore` still works as a legacy alias for it, suppressing every diagnostic on the
  line below the comment.

### Changed
- Requires the .NET 10 runtime.
