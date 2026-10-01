# GSCode engineering handbook

Start here if you are taking over GSCode. This folder explains how the system works, how its parts
fit together, and where to look for any feature. Per-file detail stays beside the code, in each
project's `FOLDER.md`; this folder links to it rather than copying it.

## Reading order

Day one, in this order:

1. [architecture-guide.md](architecture-guide.md) — the mental model: what GSC is, the layers, the
   data model from text to answers, who owns which state, the concurrency model, where new code
   goes.
2. [invariants.md](invariants.md) — the rules that must not be broken, why each exists, and what
   enforces it.
3. [lifecycles.md](lifecycles.md) — step-by-step traces: cold start, typing to diagnostics, a hover,
   completion, rename, formatting, file changes, settings, shutdown.
4. [development.md](development.md) — build, run from source, test, verify, package, CI.

Then keep these open while working:

- [features.md](features.md) — every user-visible feature mapped to its handler, engine, setting and
  tests; every custom `gscode/*` message; every command; every setting.
- [diagnostics.md](diagnostics.md) — every diagnostic code with severity, owner, message and fix.
- [glossary.md](glossary.md) — the vocabulary (context, overlay, record, store, provenance, …).
- [how-to.md](how-to.md) — recipes for common changes, and the index of the procedure skills.

## Where every kind of fact lives

Each fact has one home. When two documents disagree, the one listed here as the owner wins; fix the
other.

| Document | Owns |
|---|---|
| `docs/` (this folder) | The narrative: mental model, flows, catalogs, invariants, onboarding |
| `server/ARCHITECTURE.md` | The one-page map of the server and client, and the documentation convention |
| `server/src/<Project>/FOLDER.md` | What each source file contains and why it is shaped that way — one section per file |
| `client/src/FOLDER.md` | The same for the extension's TypeScript sources |
| `server/tests/FOLDER.md` | Every test class by keyword, test categories, every environment variable |
| `server/GAME_PROFILES.md` | The dialect matrix for every game, root discovery, bundled data, verification evidence |
| `server/FORMATTING.md` | Every formatter rule and the measurements that chose it |
| `server/PERF.md` | Budgets, measurements, and decisions taken or rejected on performance |
| `server/FOLLOWUPS.md` | Open work and decided "not doing" items — the only backlog |
| `server/samples/FOLDER.md` | The worked example scripts per game, and their `// expect` format |
| `server/tools/field-data/FOLDER.md` | Where the engine data comes from and how it is generated |
| `MULTI_GAME_API.md` | How the API library, the site and the extension serve several games |
| `data/macros/SCHEMA.md` | The BO3 macro data format |
| `.claude/skills/*/SKILL.md` | Step-by-step procedures (indexed in [how-to.md](how-to.md)) |
| `client/README.md` | The user-facing manual: setup, settings, pragmas, troubleshooting, release notes |
| `client/CHANGELOG.md` | What changed per release |
| `README.md` (root) | Project overview, supported games, issue reporting, quick build |
| Doc comments in the code | The reasoning behind a specific decision. Comments here explain *why*; read them |

## Repository layout

```
client/                 VS Code extension (TypeScript): spawns the server, commands, grammar, snippets
server/
  src/GSCode.Core/      foundation: text, positions, diagnostics, symbol keys, types, GameProfile
  src/GSCode.Parser/    per-file pipeline: lexer, preprocessor, parser, extraction (pure, no I/O)
  src/GSCode.Workspace/ database, indexes, resolution, indexing, cache, lints, completion, typing, game data
  src/GSCode.Server/    LSP host (OmniSharp): handlers, formatting, startup, transport
  tests/                Parser.Tests, Workspace.Tests, Server.Tests, and the shared GSCode.Testing
  samples/              one worked example per game, checked by tests
  tools/field-data/     generator for the bundled engine data
scripts/                Windows batch wrappers: test, sweep, corpus, perf, scale, build-vsix
data/macros/            BO3 macro data for the site
site/                   the gscode.net website (SvelteKit) — the API library shift+F1 opens
.github/workflows/      CI
```

`site/` has its own `README.md` and is not covered by this handbook.

## Keeping this folder true

- Change the narrative here when a flow, a layer or an invariant changes. Per-file changes go in
  `FOLDER.md`, not here.
- The tables in [features.md](features.md) and [diagnostics.md](diagnostics.md) each say how to
  regenerate or check them against the code. Do that when adding a handler, a message, a setting or
  a diagnostic code.
- Name code by file and symbol, never by line number.
